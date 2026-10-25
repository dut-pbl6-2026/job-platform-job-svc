using System.Reflection;
using Job.Api.Services;
using Job.Core.Entities;
using Job.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
            seedDb.OutboxEvents.Add(CreateEvent());
            await seedDb.SaveChangesAsync();
        }

        using var service = new OutboxPublisherService(
            provider, producer, NullLogger<OutboxPublisherService>.Instance);

        await InvokeProcessBatch(service);

        await using var verifyDb = CreateDb(databaseName);
        var saved = await verifyDb.OutboxEvents.SingleAsync();
        Assert.True(saved.IsPublished);
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

        using var service = new OutboxPublisherService(
            provider, producer, NullLogger<OutboxPublisherService>.Instance);

        await InvokeProcessBatch(service);

        await using var verifyDb = CreateDb(databaseName);
        var saved = await verifyDb.OutboxEvents.SingleAsync();
        Assert.False(saved.IsPublished);
        Assert.Equal(1, saved.RetryCount);
    }

    private static OutboxEvent CreateEvent()
    {
        var payload = new JobUpdatedEvent(
            Guid.NewGuid(), "Developer", "Description", Guid.NewGuid(), "Company",
            "Da Nang", null, null, "VND", null, null, "FullTime", "Entry",
            Guid.NewGuid(), null, null, "Active", DateTime.UtcNow);
        return new OutboxEvent(
            "job.updated",
            System.Text.Json.JsonSerializer.Serialize(
                EventEnvelope<JobUpdatedEvent>.Create("job.updated", payload)),
            payload.JobId.ToString());
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
        public string? Topic { get; private set; }
        public string? EventType { get; private set; }

        public Task ProduceAsync<T>(
            string topic, string key, EventEnvelope<T> envelope,
            CancellationToken ct = default)
        {
            if (ShouldFail)
                throw new InvalidOperationException("Kafka unavailable.");

            Topic = topic;
            EventType = envelope.EventType;
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }
}
