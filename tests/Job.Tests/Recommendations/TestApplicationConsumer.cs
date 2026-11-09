using System.Text.Json;
using Job.Infrastructure.Workers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SharedKernel.Events;
using SharedKernel.Kafka;

namespace Job.Tests.Recommendations;

/// <summary>
/// Exposes the consumer's protected handler so tests feed interactions the way production
/// does (Kafka message → consumer); the consume loop itself belongs to SharedKernel.
/// </summary>
internal sealed class TestApplicationConsumer(IServiceProvider services) : ApplicationInteractionsConsumer(
    Options.Create(new KafkaOptions { BootstrapServers = "" }),
    new ConfigurationBuilder().Build(),
    services.GetRequiredService<IServiceScopeFactory>(),
    NullLogger<ApplicationInteractionsConsumer>.Instance)
{
    public Task<MessageOutcome> Handle(string value) =>
        HandleMessageAsync(key: null, value, CancellationToken.None);

    /// <summary>Delivers an application.submitted event as app-svc publishes it.</summary>
    public async Task ApplyAsync(Guid applicantId, Guid jobId, DateTime? occurredAt = null)
    {
        var outcome = await Handle(Submitted(Guid.NewGuid(), jobId, applicantId, occurredAt ?? DateTime.UtcNow));
        if (outcome != MessageOutcome.Handled)
            throw new InvalidOperationException($"Consumer returned {outcome}.");
    }

    public static string Submitted(Guid applicationId, Guid jobId, Guid applicantId, DateTime occurredAt) =>
        Serialize(EventEnvelope<ApplicationSubmittedEvent>.Create(
            ApplicationEventTypes.Submitted,
            new ApplicationSubmittedEvent(applicationId, jobId, "Lập trình viên .NET", applicantId, occurredAt)));

    public static string StatusChanged(Guid applicationId, Guid jobId, Guid applicantId, DateTime occurredAt) =>
        Serialize(EventEnvelope<ApplicationStatusChangedEvent>.Create(
            ApplicationEventTypes.StatusChanged,
            new ApplicationStatusChangedEvent(
                applicationId, jobId, "Lập trình viên .NET", applicantId, "Pending", "Reviewed", Guid.NewGuid(), occurredAt)));

    public static string Serialize<T>(EventEnvelope<T> envelope) => JsonSerializer.Serialize(envelope, KafkaJson.Options);
}
