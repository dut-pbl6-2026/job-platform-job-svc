using System.Text.Json;
using Job.Core.Entities;
using Job.Infrastructure.Data;
using Job.Infrastructure.Outbox;
using Job.Infrastructure.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharedKernel.Events;
using SharedKernel.Kafka;

namespace Job.Tests;

public sealed class OutboxPublisherTests
{
    // Rows are stamped with DateTime.UtcNow when they are created (OutboxEvent), so the test clock must
    // start after that. A fixed date made every row look not-yet-due once the wall clock passed it.
    private static readonly DateTime Start = DateTime.UtcNow.AddDays(1);

    [Fact]
    public async Task ProcessBatch_PublishesEventAndMarksItPublished()
    {
        var producer = new RecordingKafkaProducer();
        await using var provider = BuildProvider(producer, out var databaseName);
        await using (var seedDb = CreateDb(databaseName))
        {
            var job = SeedJob(seedDb);
            seedDb.OutboxEvents.Add(CreateEvent(job.Id));
            await seedDb.SaveChangesAsync();
        }

        using var service = CreateService(provider, new FixedTimeProvider(Start));

        Assert.True(await service.PublishPendingAsync(CancellationToken.None));

        await using var verifyDb = CreateDb(databaseName);
        var saved = await verifyDb.OutboxEvents.SingleAsync();
        Assert.True(saved.IsPublished);
        Assert.NotNull(saved.PublishedAt);
        Assert.Equal("job.updated", producer.EventType);
        Assert.Equal("job-events", producer.Topic);
    }

    [Fact]
    public async Task ProcessBatch_WhenKafkaFails_KeepsRowAndSchedulesRetry()
    {
        var producer = new RecordingKafkaProducer { ShouldFail = true };
        await using var provider = BuildProvider(producer, out var databaseName);
        await using (var seedDb = CreateDb(databaseName))
        {
            seedDb.OutboxEvents.Add(CreateEvent());
            await seedDb.SaveChangesAsync();
        }

        var clock = new FixedTimeProvider(Start);
        using var service = CreateService(provider, clock);

        Assert.False(await service.PublishPendingAsync(CancellationToken.None));

        await using var verifyDb = CreateDb(databaseName);
        var saved = await verifyDb.OutboxEvents.SingleAsync();
        Assert.False(saved.IsPublished);
        Assert.Equal(1, saved.RetryCount);
        Assert.Equal(Start + OutboxRetryPolicy.BaseDelay, saved.NextAttemptAt);
    }

    [Fact]
    public async Task ProcessBatch_RowIsNotRetriedBeforeItsBackoffElapses()
    {
        var producer = new RecordingKafkaProducer { ShouldFail = true };
        await using var provider = BuildProvider(producer, out var databaseName);
        await using (var seedDb = CreateDb(databaseName))
        {
            seedDb.OutboxEvents.Add(CreateEvent());
            await seedDb.SaveChangesAsync();
        }

        var clock = new FixedTimeProvider(Start);
        using var service = CreateService(provider, clock);

        await service.PublishPendingAsync(CancellationToken.None);
        clock.Now = Start + OutboxRetryPolicy.BaseDelay - TimeSpan.FromSeconds(1);
        await service.PublishPendingAsync(CancellationToken.None);

        Assert.Equal(1, producer.Attempts);
    }

    [Fact]
    public async Task ProcessBatch_WhenKafkaKeepsFailing_NeverDropsTheRow_AndLogsAtError()
    {
        var producer = new RecordingKafkaProducer { ShouldFail = true };
        await using var provider = BuildProvider(producer, out var databaseName);
        Guid outboxId;
        await using (var seedDb = CreateDb(databaseName))
        {
            var row = CreateEvent();
            outboxId = row.Id;
            seedDb.OutboxEvents.Add(row);
            await seedDb.SaveChangesAsync();
        }

        var clock = new FixedTimeProvider(Start);
        var logger = new CapturingLogger();
        using var service = CreateService(provider, clock, logger);

        // Well past the old give-up point of 5 attempts: each attempt happens once its backoff has elapsed.
        const int attempts = 12;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            await service.PublishPendingAsync(CancellationToken.None);
            clock.Now = clock.Now.AddMinutes(10);
        }

        Assert.Equal(attempts, producer.Attempts);
        await using var verifyDb = CreateDb(databaseName);
        var saved = await verifyDb.OutboxEvents.SingleAsync();
        Assert.False(saved.IsPublished);
        Assert.Equal(attempts, saved.RetryCount);

        var errors = logger.Entries.Where(e => e.Level == LogLevel.Error).ToList();
        Assert.Equal(attempts - OutboxRetryPolicy.ErrorLogThreshold + 1, errors.Count);
        Assert.All(errors, e => Assert.Contains(outboxId.ToString(), e.Message));
    }

    [Fact]
    public async Task ProcessBatch_PublishesAtMostTenEventsPerCycle()
    {
        var producer = new RecordingKafkaProducer();
        await using var provider = BuildProvider(producer, out var databaseName);
        await using (var seedDb = CreateDb(databaseName))
        {
            for (var i = 0; i < 11; i++)
            {
                seedDb.OutboxEvents.Add(CreateEvent());
            }

            await seedDb.SaveChangesAsync();
        }

        using var service = CreateService(provider, new FixedTimeProvider(Start));

        await service.PublishPendingAsync(CancellationToken.None);

        Assert.Equal(10, producer.Attempts);
        await using var verifyDb = CreateDb(databaseName);
        Assert.Equal(10, await verifyDb.OutboxEvents.CountAsync(e => e.IsPublished));
        Assert.Equal(1, await verifyDb.OutboxEvents.CountAsync(e => !e.IsPublished));
    }

    [Fact]
    public async Task ProcessBatch_Cleanup_DeletesOnlyPublishedEventsOlderThanSevenDays()
    {
        var producer = new RecordingKafkaProducer();
        await using var provider = BuildProvider(producer, out var databaseName);
        await using (var seedDb = CreateDb(databaseName))
        {
            var stale = CreateEvent();
            MarkPublishedAt(stale, DateTime.UtcNow.AddDays(-8));
            var recent = CreateEvent();
            MarkPublishedAt(recent, DateTime.UtcNow.AddDays(-1));

            seedDb.OutboxEvents.AddRange(stale, recent);
            await seedDb.SaveChangesAsync();
        }

        using var service = CreateService(provider, new FixedTimeProvider(DateTime.UtcNow));

        await service.PublishPendingAsync(CancellationToken.None);

        await using var verifyDb = CreateDb(databaseName);
        var remaining = await verifyDb.OutboxEvents.ToListAsync();
        Assert.Single(remaining);
        Assert.True(remaining[0].IsPublished);
    }

    [Fact]
    public async Task ProcessBatch_JobSoftDeletedAfterEventWasQueued_PublishesJobDeleted()
    {
        // Approve writes a job.updated (Active) outbox row; the recruiter deletes the job
        // before the next poll. Publishing the stale snapshot re-indexed the deleted job.
        var producer = new RecordingKafkaProducer();
        await using var provider = BuildProvider(producer, out var databaseName);
        Guid jobId;
        await using (var seedDb = CreateDb(databaseName))
        {
            var job = SeedJob(seedDb);
            jobId = job.Id;
            seedDb.OutboxEvents.Add(CreateEvent(job.Id, status: "Active"));
            job.SoftDelete();
            await seedDb.SaveChangesAsync();
        }

        using var service = CreateService(provider, new FixedTimeProvider(Start));

        await service.PublishPendingAsync(CancellationToken.None);

        Assert.Equal(JobEventTypes.Deleted, producer.EventType);
        var payload = Assert.IsType<JobDeletedEvent>(producer.Payload);
        Assert.Equal(jobId, payload.JobId);
        Assert.Equal(jobId.ToString(), producer.Key);
        await using var verifyDb = CreateDb(databaseName);
        Assert.True((await verifyDb.OutboxEvents.SingleAsync()).IsPublished);
    }

    [Fact]
    public async Task ProcessBatch_JobNoLongerExists_PublishesJobDeleted()
    {
        var producer = new RecordingKafkaProducer();
        await using var provider = BuildProvider(producer, out var databaseName);
        var jobId = Guid.NewGuid();
        await using (var seedDb = CreateDb(databaseName))
        {
            seedDb.OutboxEvents.Add(CreateEvent(jobId));
            await seedDb.SaveChangesAsync();
        }

        using var service = CreateService(provider, new FixedTimeProvider(Start));

        await service.PublishPendingAsync(CancellationToken.None);

        Assert.Equal(JobEventTypes.Deleted, producer.EventType);
        Assert.Equal(jobId, Assert.IsType<JobDeletedEvent>(producer.Payload).JobId);
    }

    [Fact]
    public async Task ProcessBatch_PublishesCurrentJobStateInsteadOfQueuedSnapshot()
    {
        var producer = new RecordingKafkaProducer();
        await using var provider = BuildProvider(producer, out var databaseName);
        await using (var seedDb = CreateDb(databaseName))
        {
            var job = SeedJob(seedDb, title: "Edited after approval");
            seedDb.OutboxEvents.Add(CreateEvent(job.Id, status: "Rejected"));
            await seedDb.SaveChangesAsync();
        }

        using var service = CreateService(provider, new FixedTimeProvider(Start));

        await service.PublishPendingAsync(CancellationToken.None);

        Assert.Equal(JobEventTypes.Updated, producer.EventType);
        var payload = Assert.IsType<JobUpdatedEvent>(producer.Payload);
        Assert.Equal("Edited after approval", payload.Title);
        Assert.Equal("Active", payload.Status);
    }

    [Fact]
    public async Task ProcessBatch_CreatedRow_PublishesJobCreatedWithCurrentState()
    {
        var producer = new RecordingKafkaProducer();
        await using var provider = BuildProvider(producer, out var databaseName);
        Guid jobId;
        await using (var seedDb = CreateDb(databaseName))
        {
            var job = SeedJob(seedDb, title: "Created job");
            jobId = job.Id;
            seedDb.OutboxEvents.Add(JobOutboxEvents.Created(job, "Outbox Corp", null));
            await seedDb.SaveChangesAsync();
        }

        using var service = CreateService(provider, new FixedTimeProvider(Start));

        await service.PublishPendingAsync(CancellationToken.None);

        Assert.Equal(JobEventTypes.Created, producer.EventType);
        var payload = Assert.IsType<JobCreatedEvent>(producer.Payload);
        Assert.Equal(jobId, payload.JobId);
        Assert.Equal("Created job", payload.Title);
        Assert.Equal(jobId.ToString(), producer.Key);
    }

    [Fact]
    public async Task ProcessBatch_DeletedRow_PublishesStoredEnvelopeUnchanged()
    {
        var producer = new RecordingKafkaProducer();
        await using var provider = BuildProvider(producer, out var databaseName);
        var jobId = Guid.NewGuid();
        Guid storedEventId;
        await using (var seedDb = CreateDb(databaseName))
        {
            var row = JobOutboxEvents.Deleted(jobId);
            seedDb.OutboxEvents.Add(row);
            await seedDb.SaveChangesAsync();
            storedEventId = JsonDocument.Parse(row.Payload).RootElement.GetProperty("eventId").GetGuid();
        }

        using var service = CreateService(provider, new FixedTimeProvider(Start));

        await service.PublishPendingAsync(CancellationToken.None);

        Assert.Equal(JobEventTypes.Deleted, producer.EventType);
        Assert.Equal(storedEventId, producer.EventId);
        Assert.Equal(jobId, Assert.IsType<JobDeletedEvent>(producer.Payload).JobId);
    }

    [Fact]
    public async Task PublishPending_EmptyOutbox_ReturnsFalseAndProducesNothing()
    {
        var producer = new RecordingKafkaProducer();
        await using var provider = BuildProvider(producer, out _);

        using var service = CreateService(provider, new FixedTimeProvider(Start));

        Assert.False(await service.PublishPendingAsync(CancellationToken.None));
        Assert.Equal(0, producer.Attempts);
    }

    [Fact]
    public async Task ProcessBatch_FailureMidBatch_StopsCycleAndLeavesRemainingRowForLater()
    {
        // The first publish succeeds, the second fails: the cycle must stop there, so the third row
        // is never attempted in this cycle.
        var producer = new RecordingKafkaProducer { FailFromAttempt = 2 };
        await using var provider = BuildProvider(producer, out var databaseName);
        await using (var seedDb = CreateDb(databaseName))
        {
            for (var i = 0; i < 3; i++)
                seedDb.OutboxEvents.Add(CreateEvent(Guid.NewGuid()));
            await seedDb.SaveChangesAsync();
        }

        using var service = CreateService(provider, new FixedTimeProvider(Start));

        Assert.False(await service.PublishPendingAsync(CancellationToken.None));

        Assert.Equal(2, producer.Attempts);
        await using var verifyDb = CreateDb(databaseName);
        var rows = await verifyDb.OutboxEvents.ToListAsync();
        Assert.Equal(1, rows.Count(r => r.IsPublished));
        Assert.Equal(2, rows.Count(r => !r.IsPublished));
        Assert.Equal(1, rows.Single(r => !r.IsPublished && r.RetryCount == 1).RetryCount);
        Assert.Equal(1, rows.Count(r => !r.IsPublished && r.RetryCount == 0));
    }

    [Fact]
    public async Task ProcessBatch_UnsupportedEventType_IsNeverDroppedAndNeverProduced()
    {
        var producer = new RecordingKafkaProducer();
        await using var provider = BuildProvider(producer, out var databaseName);
        await using (var seedDb = CreateDb(databaseName))
        {
            seedDb.OutboxEvents.Add(new OutboxEvent("job.unknown", "{}", Guid.NewGuid().ToString()));
            await seedDb.SaveChangesAsync();
        }

        var clock = new FixedTimeProvider(Start);
        using var service = CreateService(provider, clock);

        Assert.False(await service.PublishPendingAsync(CancellationToken.None));

        Assert.Equal(0, producer.Attempts);
        await using var verifyDb = CreateDb(databaseName);
        var saved = await verifyDb.OutboxEvents.SingleAsync();
        Assert.False(saved.IsPublished);
        Assert.Equal(1, saved.RetryCount);
        Assert.Equal(Start + OutboxRetryPolicy.BaseDelay, saved.NextAttemptAt);
    }

    private static OutboxPublisherService CreateService(
        ServiceProvider provider, TimeProvider clock, ILogger<OutboxPublisherService>? logger = null) =>
        new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IKafkaProducer>(),
            logger ?? new CapturingLogger(),
            clock);

    private static Core.Entities.Job SeedJob(JobDbContext db, string title = "Developer")
    {
        var company = new Company("Outbox Corp", Guid.NewGuid());
        var job = new Core.Entities.Job(title, "Description", company.Id, "Da Nang", Guid.NewGuid(), JobStatus.Active);
        db.Companies.Add(company);
        db.Jobs.Add(job);
        return job;
    }

    private static OutboxEvent CreateEvent(Guid? jobId = null, string status = "Active")
    {
        var payload = new JobUpdatedEvent(
            jobId ?? Guid.NewGuid(), "Developer", "Description", Guid.NewGuid(), "Company",
            "Da Nang", null, null, "VND", null, null, "FullTime", "Entry",
            Guid.NewGuid(), null, null, status, DateTime.UtcNow);
        return new OutboxEvent(
            "job.updated",
            JsonSerializer.Serialize(
                EventEnvelope<JobUpdatedEvent>.Create("job.updated", payload),
                KafkaJson.Options),
            payload.JobId.ToString());
    }

    private static void MarkPublishedAt(OutboxEvent outboxEvent, DateTime publishedAt)
    {
        outboxEvent.MarkPublished();
        typeof(OutboxEvent)
            .GetProperty(nameof(OutboxEvent.PublishedAt))!
            .GetSetMethod(nonPublic: true)!
            .Invoke(outboxEvent, [publishedAt]);
    }

    private static ServiceProvider BuildProvider(
        RecordingKafkaProducer producer, out string databaseName)
    {
        var name = Guid.NewGuid().ToString();
        databaseName = name;
        var services = new ServiceCollection();
        services.AddSingleton<IKafkaProducer>(producer);
        services.AddDbContext<JobDbContext>(options => options.UseInMemoryDatabase(name));
        return services.BuildServiceProvider();
    }

    private static JobDbContext CreateDb(string databaseName)
    {
        var options = new DbContextOptionsBuilder<JobDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;
        return new JobDbContext(options);
    }

    private sealed class FixedTimeProvider(DateTime now) : TimeProvider
    {
        public DateTime Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(Now, DateTimeKind.Utc));
    }

    private sealed class CapturingLogger : ILogger<OutboxPublisherService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class RecordingKafkaProducer : IKafkaProducer
    {
        public bool ShouldFail { get; init; }
        /// <summary>When set, publish attempts numbered this and later (1-based) fail; earlier ones succeed.</summary>
        public int? FailFromAttempt { get; init; }
        public int Attempts { get; private set; }
        public string? Topic { get; private set; }
        public string? EventType { get; private set; }
        public string? Key { get; private set; }
        public Guid EventId { get; private set; }
        public object? Payload { get; private set; }

        public Task ProduceAsync<T>(
            string topic, string key, EventEnvelope<T> envelope,
            CancellationToken ct = default)
        {
            Attempts++;
            if (ShouldFail || (FailFromAttempt is int n && Attempts >= n))
                throw new InvalidOperationException("Kafka unavailable.");

            Topic = topic;
            Key = key;
            EventType = envelope.EventType;
            EventId = envelope.EventId;
            Payload = envelope.Payload;
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }
}
