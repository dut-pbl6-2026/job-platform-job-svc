using System.Text.Json;
using Job.Core.Entities;
using Job.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SharedKernel.Events;
using SharedKernel.Kafka;

namespace Job.Infrastructure.Workers;

/// <summary>
/// BackgroundService that publishes OutboxEvent rows to the <c>job-events</c> topic.
///
/// Delivery is at-least-once: a row is marked published only after Kafka acknowledges it, so a crash
/// between the acknowledgement and the commit republishes the row. Consumers must tolerate duplicates.
/// Multiple replicas are safe: on PostgreSQL the batch is claimed with <c>FOR UPDATE SKIP LOCKED</c>
/// inside one transaction, and a row is never skipped for good: failures back off exponentially
/// (capped) and are logged at Error with the row id once they keep failing.
///
/// Poll interval: 5 seconds (no wait while a cycle still publishes rows).
/// Batch size: 10 claimable rows per cycle.
/// Cleanup: IsPublished=true events older than 7 days are deleted after each cycle.
/// </summary>
public sealed class OutboxPublisherService : BackgroundService
{
    private const int PollIntervalMs = 5_000;
    private const int BatchSize = 10;
    private static readonly TimeSpan CleanupThreshold = TimeSpan.FromDays(7);
    private const string JobEventsTopic = "job-events";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IKafkaProducer _kafkaProducer;
    private readonly ILogger<OutboxPublisherService> _logger;
    private readonly TimeProvider _timeProvider;

    public OutboxPublisherService(
        IServiceScopeFactory scopeFactory,
        IKafkaProducer kafkaProducer,
        ILogger<OutboxPublisherService> logger,
        TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory;
        _kafkaProducer = kafkaProducer;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "OutboxPublisherService started. PollInterval={PollIntervalMs}ms BatchSize={BatchSize}.",
            PollIntervalMs, BatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            var progressed = false;
            try
            {
                progressed = await PublishPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "OutboxPublisherService: Unexpected error in poll loop. Will retry next cycle.");
            }

            if (progressed)
                continue; // backlog: publish the next batch without waiting

            try
            {
                await Task.Delay(PollIntervalMs, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("OutboxPublisherService stopped.");
    }

    /// <summary>
    /// Runs one publish cycle (the background loop calls it every poll). Returns true when the cycle
    /// published at least one row and no row failed.
    /// </summary>
    public async Task<bool> PublishPendingAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JobDbContext>();
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        bool progressed;
        if (db.Database.IsRelational())
        {
            // The claimed rows stay row-locked until commit, so no other replica publishes them meanwhile.
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var batch = await ClaimRelationalAsync(db, now, ct);
            _logger.LogDebug("OutboxPublisherService: Claimed {Count} outbox events.", batch.Count);
            progressed = await PublishBatchAsync(batch, db, ct);
            await transaction.CommitAsync(ct);
        }
        else
        {
            // Non-relational providers (EF InMemory, used by unit tests) have no row locks.
            var unpublished = await db.OutboxEvents
                .Where(e => !e.IsPublished)
                .OrderBy(e => e.CreatedAt).ThenBy(e => e.Id)
                .ToListAsync(ct);
            var batch = OutboxClaimPolicy.SelectClaimable(unpublished, now, BatchSize);
            progressed = await PublishBatchAsync(batch, db, ct);
        }

        await CleanupPublishedEventsAsync(db, ct);
        return progressed;
    }

    /// <summary>
    /// Claims up to <see cref="BatchSize"/> due rows with <c>FOR UPDATE SKIP LOCKED</c>. The
    /// <c>NOT EXISTS</c> clause keeps per-job order: a row waits while an earlier unpublished row of the
    /// same job exists. Same rules as <see cref="OutboxClaimPolicy.SelectClaimable"/>.
    /// Must run inside a transaction.
    /// </summary>
    private static async Task<List<OutboxEvent>> ClaimRelationalAsync(
        JobDbContext db, DateTime now, CancellationToken ct)
    {
        var ids = await db.Database.SqlQuery<Guid>($"""
            SELECT o."Id" FROM "OutboxEvents" AS o
            WHERE NOT o."IsPublished"
              AND o."NextAttemptAt" <= {now}
              AND NOT EXISTS (
                  SELECT 1 FROM "OutboxEvents" AS p
                  WHERE NOT p."IsPublished"
                    AND p."AggregateId" = o."AggregateId"
                    AND (p."CreatedAt", p."Id") < (o."CreatedAt", o."Id"))
            ORDER BY o."CreatedAt", o."Id"
            LIMIT {BatchSize}
            FOR UPDATE OF o SKIP LOCKED
            """).ToListAsync(ct);

        if (ids.Count == 0)
            return [];

        var rows = await db.OutboxEvents.Where(e => ids.Contains(e.Id)).ToListAsync(ct);
        return rows.OrderBy(e => e.CreatedAt).ThenBy(e => e.Id).ToList();
    }

    /// <summary>
    /// Publishes rows in order and stops at the first failure, so a Kafka outage costs one publish
    /// timeout per cycle instead of one per row. Returns true when every row in the batch was published.
    /// </summary>
    private async Task<bool> PublishBatchAsync(
        List<OutboxEvent> batch, JobDbContext db, CancellationToken ct)
    {
        if (batch.Count == 0)
            return false;

        foreach (var outboxEvent in batch)
        {
            if (!await TryPublishAsync(outboxEvent, db, ct))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Publishes one row and marks it published after the Kafka acknowledgement. On failure the row
    /// stays unpublished and is rescheduled with backoff. It is never dropped.
    /// </summary>
    private async Task<bool> TryPublishAsync(OutboxEvent outboxEvent, JobDbContext db, CancellationToken ct)
    {
        var key = outboxEvent.AggregateId ?? outboxEvent.Id.ToString();

        try
        {
            await PublishEventAsync(outboxEvent, key, db, ct);

            outboxEvent.MarkPublished();
            await db.SaveChangesAsync(ct);

            _logger.LogDebug(
                "OutboxPublisherService: Published {EventType} AggregateId={AggregateId} OutboxId={OutboxId}.",
                outboxEvent.EventType, outboxEvent.AggregateId, outboxEvent.Id);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var nextAttemptAt = _timeProvider.GetUtcNow().UtcDateTime
                .Add(OutboxRetryPolicy.DelayAfterFailure(outboxEvent.RetryCount + 1));
            outboxEvent.RecordFailure(nextAttemptAt);
            await db.SaveChangesAsync(CancellationToken.None); // persist the retry state even on cancel

            if (outboxEvent.RetryCount >= OutboxRetryPolicy.ErrorLogThreshold)
            {
                _logger.LogError(ex,
                    "OutboxPublisherService: Event {OutboxId} ({EventType}) has failed {RetryCount} times. " +
                    "Row kept unpublished; next attempt at {NextAttemptAt:O}.",
                    outboxEvent.Id, outboxEvent.EventType, outboxEvent.RetryCount, nextAttemptAt);
            }
            else
            {
                _logger.LogWarning(ex,
                    "OutboxPublisherService: Failed to publish {EventType} OutboxId={OutboxId}. RetryCount={RetryCount}.",
                    outboxEvent.EventType, outboxEvent.Id, outboxEvent.RetryCount);
            }
            return false;
        }
    }

    private async Task PublishEventAsync(OutboxEvent outboxEvent, string key, JobDbContext db, CancellationToken ct)
    {
        switch (outboxEvent.EventType)
        {
            case JobEventTypes.Created:
            case JobEventTypes.Updated:
                // Both payloads share the JobUpdatedEvent shape, so the job id reads the same way.
                var jobId = Deserialize<EventEnvelope<JobUpdatedEvent>>(outboxEvent).Payload.JobId;
                await PublishCurrentStateAsync(jobId, outboxEvent.EventType, key, db, ct);
                return;

            case JobEventTypes.Deleted:
                // The stored envelope is published as written: same EventId and timestamp on every retry.
                var deleted = Deserialize<EventEnvelope<JobDeletedEvent>>(outboxEvent);
                await _kafkaProducer.ProduceAsync(JobEventsTopic, key, deleted, ct);
                return;

            default:
                throw new InvalidOperationException(
                    $"Unsupported outbox event type '{outboxEvent.EventType}'.");
        }
    }

    private static T Deserialize<T>(OutboxEvent outboxEvent) =>
        JsonSerializer.Deserialize<T>(outboxEvent.Payload, KafkaJson.Options)
        ?? throw new InvalidOperationException($"Outbox event {outboxEvent.Id} has an empty payload.");

    /// <summary>
    /// Publishes the job as it is NOW, not the snapshot stored in the outbox row.
    /// A stale snapshot published after a later event re-indexed a deleted job as Active in
    /// search-svc. A job that is soft-deleted or no longer exists is published as <c>job.deleted</c>.
    /// </summary>
    private async Task PublishCurrentStateAsync(
        Guid jobId, string eventType, string key, JobDbContext db, CancellationToken ct)
    {
        var job = await db.Jobs.IgnoreQueryFilters()
            .AsNoTracking()
            .Include(j => j.Company)
            .Include(j => j.Category)
            .FirstOrDefaultAsync(j => j.Id == jobId, ct);

        if (job is null || job.Status == JobStatus.Deleted)
        {
            var deleted = EventEnvelope<JobDeletedEvent>.Create(
                JobEventTypes.Deleted, new JobDeletedEvent(jobId, DateTime.UtcNow));
            await _kafkaProducer.ProduceAsync(JobEventsTopic, key, deleted, ct);
            return;
        }

        if (eventType == JobEventTypes.Created)
        {
            var created = new JobCreatedEvent(
                job.Id, job.Title, job.Description, job.CompanyId, job.Company?.Name ?? "",
                job.Location, job.SalaryMin, job.SalaryMax, job.SalaryCurrency,
                job.CategoryId, job.Category?.Name, job.EmploymentType, job.ExperienceLevel,
                job.RecruiterId, job.Requirements, job.Benefits,
                job.Status.ToString(), DateTime.UtcNow, job.CreatedAt);
            await _kafkaProducer.ProduceAsync(
                JobEventsTopic, key, EventEnvelope<JobCreatedEvent>.Create(JobEventTypes.Created, created), ct);
            return;
        }

        var updated = new JobUpdatedEvent(
            job.Id, job.Title, job.Description, job.CompanyId, job.Company?.Name ?? "",
            job.Location, job.SalaryMin, job.SalaryMax, job.SalaryCurrency,
            job.CategoryId, job.Category?.Name, job.EmploymentType, job.ExperienceLevel,
            job.RecruiterId, job.Requirements, job.Benefits,
            job.Status.ToString(), DateTime.UtcNow, job.CreatedAt);
        await _kafkaProducer.ProduceAsync(
            JobEventsTopic, key, EventEnvelope<JobUpdatedEvent>.Create(JobEventTypes.Updated, updated), ct);
    }

    private async Task CleanupPublishedEventsAsync(JobDbContext db, CancellationToken ct)
    {
        var cutoff = _timeProvider.GetUtcNow().UtcDateTime - CleanupThreshold;
        var candidates = db.OutboxEvents
            .Where(e => e.IsPublished && e.PublishedAt < cutoff);
        int deleted;

        if (!db.Database.IsRelational())
        {
            var staleEvents = await candidates.ToListAsync(ct);
            db.OutboxEvents.RemoveRange(staleEvents);
            deleted = staleEvents.Count;
            if (deleted > 0)
                await db.SaveChangesAsync(ct);
        }
        else
        {
            deleted = await candidates.ExecuteDeleteAsync(ct);
        }

        if (deleted > 0)
        {
            _logger.LogInformation(
                "OutboxPublisherService: Cleaned up {Count} published outbox events older than {Days} days.",
                deleted, CleanupThreshold.Days);
        }
    }
}
