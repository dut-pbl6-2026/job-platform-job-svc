using System.Text.Json;
using Job.Core.Entities;
using Job.Infrastructure.Data;
using Job.Infrastructure.Recommendations;
using Job.Infrastructure.Workers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SharedKernel.Events;
using SharedKernel.Kafka;

namespace Job.Tests.Recommendations;

/// <summary>
/// PBL6-10 slice 3: application-events → JobInteractions. Messages are built with the
/// SharedKernel contract (EventEnvelope + KafkaJson), exactly as app-svc publishes them.
/// </summary>
public sealed class ApplicationInteractionsConsumerTests : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly TestConsumer _consumer;

    public ApplicationInteractionsConsumerTests()
    {
        var dbName = Guid.NewGuid().ToString();
        _services = new ServiceCollection()
            .AddDbContext<JobDbContext>(o => o.UseInMemoryDatabase(dbName))
            .AddScoped<InteractionWriter>()
            .BuildServiceProvider();
        _consumer = new TestConsumer(_services);
    }

    public void Dispose() => _services.Dispose();

    [Fact]
    public async Task Submitted_StoresAppliedInteraction()
    {
        var applicationId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var applicantId = Guid.NewGuid();
        var occurredAt = new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);

        var outcome = await _consumer.Handle(Submitted(applicationId, jobId, applicantId, occurredAt));

        Assert.Equal(MessageOutcome.Handled, outcome);
        var row = Assert.Single(await Rows());
        Assert.Equal(applicantId, row.UserId);
        Assert.Equal(jobId, row.JobId);
        Assert.Equal(InteractionType.Applied, row.Type);
        Assert.Equal(applicationId, row.SourceId);
        Assert.Equal(occurredAt, row.OccurredAt);
    }

    [Fact]
    public async Task RedeliveredEvent_DoesNotDuplicate()
    {
        var message = Submitted(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow);

        Assert.Equal(MessageOutcome.Handled, await _consumer.Handle(message));
        Assert.Equal(MessageOutcome.Handled, await _consumer.Handle(message));

        Assert.Single(await Rows());
    }

    [Fact]
    public async Task StatusChanged_FillsInteractionWhenSubmittedWasLost()
    {
        var applicantId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        var outcome = await _consumer.Handle(StatusChanged(Guid.NewGuid(), jobId, applicantId, DateTime.UtcNow));

        Assert.Equal(MessageOutcome.Handled, outcome);
        var row = Assert.Single(await Rows());
        Assert.Equal(applicantId, row.UserId);
        Assert.Equal(jobId, row.JobId);
    }

    [Fact]
    public async Task LegacyApplicationUpdatedName_IsAccepted()
    {
        var payload = new ApplicationStatusChangedEvent(
            Guid.NewGuid(), Guid.NewGuid(), "Dev", Guid.NewGuid(), "Pending", "Reviewed", Guid.NewGuid(), DateTime.UtcNow);
        var message = Serialize(EventEnvelope<ApplicationStatusChangedEvent>.Create("application.updated", payload));

        Assert.Equal(MessageOutcome.Handled, await _consumer.Handle(message));
        Assert.Single(await Rows());
    }

    [Fact]
    public async Task EmptyApplicantId_IsSkipped()
    {
        var outcome = await _consumer.Handle(Submitted(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, DateTime.UtcNow));

        Assert.Equal(MessageOutcome.Skip, outcome);
        Assert.Empty(await Rows());
    }

    [Fact]
    public async Task EmptyJobId_IsSkipped()
    {
        var outcome = await _consumer.Handle(Submitted(Guid.NewGuid(), Guid.Empty, Guid.NewGuid(), DateTime.UtcNow));

        Assert.Equal(MessageOutcome.Skip, outcome);
    }

    [Fact]
    public async Task MissingOccurredAt_IsSkipped()
    {
        var outcome = await _consumer.Handle(Submitted(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), default));

        Assert.Equal(MessageOutcome.Skip, outcome);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"eventType":"application.submitted"}""")]
    [InlineData("""{"eventType":"application.submitted","payload":"oops"}""")]
    public async Task MalformedMessage_IsSkipped(string message)
    {
        Assert.Equal(MessageOutcome.Skip, await _consumer.Handle(message));
    }

    [Fact]
    public async Task UnknownEventType_IsSkipped()
    {
        var payload = new ApplicationSubmittedEvent(Guid.NewGuid(), Guid.NewGuid(), "Dev", Guid.NewGuid(), DateTime.UtcNow);
        var message = Serialize(EventEnvelope<ApplicationSubmittedEvent>.Create("application.withdrawn", payload));

        Assert.Equal(MessageOutcome.Skip, await _consumer.Handle(message));
        Assert.Empty(await Rows());
    }

    [Fact]
    public async Task DatabaseFailure_IsRetried()
    {
        using var broken = new ServiceCollection()
            // Port 1 refuses immediately: deterministic connection failure.
            .AddDbContext<JobDbContext>(o => o.UseNpgsql("Host=127.0.0.1;Port=1;Timeout=1;Command Timeout=1"))
            .AddScoped<InteractionWriter>()
            .BuildServiceProvider();
        var consumer = new TestConsumer(broken);

        var outcome = await consumer.Handle(Submitted(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTime.UtcNow));

        Assert.Equal(MessageOutcome.Retry, outcome);
    }

    private async Task<List<JobInteraction>> Rows()
    {
        using var scope = _services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<JobDbContext>().JobInteractions.ToListAsync();
    }

    private static string Submitted(Guid applicationId, Guid jobId, Guid applicantId, DateTime occurredAt) =>
        Serialize(EventEnvelope<ApplicationSubmittedEvent>.Create(
            ApplicationEventTypes.Submitted,
            new ApplicationSubmittedEvent(applicationId, jobId, "Lập trình viên .NET", applicantId, occurredAt)));

    private static string StatusChanged(Guid applicationId, Guid jobId, Guid applicantId, DateTime occurredAt) =>
        Serialize(EventEnvelope<ApplicationStatusChangedEvent>.Create(
            ApplicationEventTypes.StatusChanged,
            new ApplicationStatusChangedEvent(
                applicationId, jobId, "Lập trình viên .NET", applicantId, "Pending", "Reviewed", Guid.NewGuid(), occurredAt)));

    private static string Serialize<T>(EventEnvelope<T> envelope) => JsonSerializer.Serialize(envelope, KafkaJson.Options);

    /// <summary>Exposes the protected handler; the consume loop itself belongs to SharedKernel.</summary>
    private sealed class TestConsumer(IServiceProvider services) : ApplicationInteractionsConsumer(
        Options.Create(new KafkaOptions { BootstrapServers = "" }),
        new ConfigurationBuilder().Build(),
        services.GetRequiredService<IServiceScopeFactory>(),
        NullLogger<ApplicationInteractionsConsumer>.Instance)
    {
        public Task<MessageOutcome> Handle(string value) =>
            HandleMessageAsync(key: null, value, CancellationToken.None);
    }
}
