using System.Reflection;
using Job.Core.Entities;
using Job.Infrastructure.Data;
using Job.Infrastructure.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SharedKernel.Events;
using SharedKernel.Kafka;

namespace Job.Tests;

public sealed class OutboxPublisherTests
{
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

        using var service = CreateService(provider, producer);

        await InvokeProcessBatch(service);

        await using var verifyDb = CreateDb(databaseName);
        var saved = await verifyDb.OutboxEvents.SingleAsync();
        Assert.True(saved.IsPublished);
        Assert.NotNull(saved.PublishedAt);
        Assert.Equal("job.updated", producer.EventType);
        Assert.Equal("job-events", producer.Topic);
    }

    [Fact]
    public async Task ProcessBatch_WhenKafkaFails_IncrementsRetryCount()
    {
        var producer = new RecordingKafkaProducer { ShouldFail = true };
        await using var provider = BuildProvider(producer, out var databaseName);
        await using (var seedDb = CreateDb(databaseName))
        {
            seedDb.OutboxEvents.Add(CreateEvent());
            await seedDb.SaveChangesAsync();
        }

        using var service = CreateService(provider, producer);

        await InvokeProcessBatch(service);

        await using var verifyDb = CreateDb(databaseName);
        var saved = await verifyDb.OutboxEvents.SingleAsync();
        Assert.False(saved.IsPublished);
        Assert.Equal(1, saved.RetryCount);
    }

    [Fact]
    public async Task ProcessBatch_AfterMaxRetries_StopsRetryingEvent()
    {
        var producer = new RecordingKafkaProducer { ShouldFail = true };
        await using var provider = BuildProvider(producer, out var databaseName);
        await using (var seedDb = CreateDb(databaseName))
        {
            seedDb.OutboxEvents.Add(CreateEvent());
            await seedDb.SaveChangesAsync();
        }

        using var service = CreateService(provider, producer);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await InvokeProcessBatch(service);
        }

        Assert.Equal(5, producer.Attempts);

        // RetryCount == MaxRetries: the event is excluded from the next batch.
        await InvokeProcessBatch(service);

        Assert.Equal(5, producer.Attempts);
        await using var verifyDb = CreateDb(databaseName);
        var saved = await verifyDb.OutboxEvents.SingleAsync();
        Assert.Equal(5, saved.RetryCount);
        Assert.False(saved.IsPublished);
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

        using var service = CreateService(provider, producer);

        await InvokeProcessBatch(service);

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

        using var service = CreateService(provider, producer);

        await InvokeProcessBatch(service);

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

        using var service = CreateService(provider, producer);

        await InvokeProcessBatch(service);

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

        using var service = CreateService(provider, producer);

        await InvokeProcessBatch(service);

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

        using var service = CreateService(provider, producer);

        await InvokeProcessBatch(service);

        Assert.Equal(JobEventTypes.Updated, producer.EventType);
        var payload = Assert.IsType<JobUpdatedEvent>(producer.Payload);
        Assert.Equal("Edited after approval", payload.Title);
        Assert.Equal("Active", payload.Status);
    }

    private static OutboxPublisherService CreateService(
        ServiceProvider provider, IKafkaProducer producer) =>
        new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            producer,
            NullLogger<OutboxPublisherService>.Instance);

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
            System.Text.Json.JsonSerializer.Serialize(
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

    private static async Task InvokeProcessBatch(OutboxPublisherService service)
    {
        var method = typeof(OutboxPublisherService).GetMethod(
            "ProcessBatchAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(service, [CancellationToken.None])!;
    }

    private sealed class RecordingKafkaProducer : IKafkaProducer
    {
        public bool ShouldFail { get; init; }
        public int Attempts { get; private set; }
        public string? Topic { get; private set; }
        public string? EventType { get; private set; }
        public string? Key { get; private set; }
        public object? Payload { get; private set; }

        public Task ProduceAsync<T>(
            string topic, string key, EventEnvelope<T> envelope,
            CancellationToken ct = default)
        {
            Attempts++;
            if (ShouldFail)
                throw new InvalidOperationException("Kafka unavailable.");

            Topic = topic;
            Key = key;
            EventType = envelope.EventType;
            Payload = envelope.Payload;
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }
}
