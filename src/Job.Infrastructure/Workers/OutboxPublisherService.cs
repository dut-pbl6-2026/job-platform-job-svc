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
/// BackgroundService that polls the OutboxEvent table and publishes pending events
/// to Kafka (feat(admin): Slice 4 — transactional outbox pattern, R-1 dual-write fix).
///
/// Poll interval: 5 seconds.
/// Batch size: 10 events per cycle.
/// Retry policy: MaxRetries (5) attempts; events exceeding this are skipped + ERROR logged.
/// Cleanup: IsPublished=true events older than 7 days are deleted after each cycle.
/// </summary>
public sealed class OutboxPublisherService : BackgroundService
{
    private const int PollIntervalMs = 5_000;
    private const int BatchSize = 10;
    private const int MaxRetries = 5;
    private static readonly TimeSpan CleanupThreshold = TimeSpan.FromDays(7);
    private const string JobEventsTopic = "job-events";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IKafkaProducer _kafkaProducer;
    private readonly ILogger<OutboxPublisherService> _logger;

    public OutboxPublisherService(
        IServiceScopeFactory scopeFactory,
        IKafkaProducer kafkaProducer,
        ILogger<OutboxPublisherService> logger)
    {
        _scopeFactory = scopeFactory;
        _kafkaProducer = kafkaProducer;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "OutboxPublisherService started. PollInterval={PollIntervalMs}ms BatchSize={BatchSize}.",
            PollIntervalMs, BatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);
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

    private async Task ProcessBatchAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JobDbContext>();

        // Fetch top unpublished events, skipping those beyond MaxRetries
        var events = await db.OutboxEvents
            .Where(e => !e.IsPublished && e.RetryCount < MaxRetries)
            .OrderBy(e => e.CreatedAt)
            .Take(BatchSize)
            .ToListAsync(ct);

        if (events.Count == 0)
        {
            await CleanupPublishedEventsAsync(db, ct);
            return;
        }

        _logger.LogDebug("OutboxPublisherService: Processing {Count} pending outbox events.", events.Count);

        foreach (var outboxEvent in events)
        {
            await PublishEventAsync(outboxEvent, db, ct);
        }

        await CleanupPublishedEventsAsync(db, ct);
    }

    private async Task PublishEventAsync(OutboxEvent outboxEvent, JobDbContext db, CancellationToken ct)
    {
        var key = outboxEvent.AggregateId ?? outboxEvent.Id.ToString();

        try
        {
            if (!string.Equals(outboxEvent.EventType, JobEventTypes.Updated, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Unsupported outbox event type '{outboxEvent.EventType}'.");
            }

            var envelope = JsonSerializer.Deserialize<EventEnvelope<JobUpdatedEvent>>(
                outboxEvent.Payload, KafkaJson.Options)
                ?? throw new InvalidOperationException(
                    $"Outbox event {outboxEvent.Id} has an empty payload.");

            await _kafkaProducer.ProduceAsync(JobEventsTopic, key, envelope, ct);

            outboxEvent.MarkPublished();
            await db.SaveChangesAsync(ct);

            _logger.LogDebug(
                "OutboxPublisherService: Published {EventType} AggregateId={AggregateId} OutboxId={OutboxId}.",
                outboxEvent.EventType, outboxEvent.AggregateId, outboxEvent.Id);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            outboxEvent.IncrementRetry();
            await db.SaveChangesAsync(CancellationToken.None); // persist retry count even on cancel

            var remaining = MaxRetries - outboxEvent.RetryCount;
            if (remaining <= 0)
            {
                _logger.LogError(ex,
                    "OutboxPublisherService: Event {OutboxId} ({EventType}) exceeded {MaxRetries} retries. Permanently skipped.",
                    outboxEvent.Id, outboxEvent.EventType, MaxRetries);
            }
            else
            {
                _logger.LogWarning(ex,
                    "OutboxPublisherService: Failed to publish {EventType} OutboxId={OutboxId}. RetryCount={RetryCount} Remaining={Remaining}.",
                    outboxEvent.EventType, outboxEvent.Id, outboxEvent.RetryCount, remaining);
            }
        }
    }

    private async Task CleanupPublishedEventsAsync(JobDbContext db, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - CleanupThreshold;
        var candidates = db.OutboxEvents
            .Where(e => e.IsPublished && e.PublishedAt < cutoff);
        int deleted;

        if (db.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")
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
