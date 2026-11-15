using SharedKernel.Events;
using SharedKernel.Kafka;

namespace Job.Api.Services;

/// <summary>
/// No-op Kafka producer used when <c>KAFKA_BOOTSTRAP_SERVERS</c> is not set.
/// Every publish fails, so <see cref="Job.Infrastructure.Workers.OutboxPublisherService"/> keeps the
/// rows unpublished and retries them once Kafka is configured.
/// </summary>
public sealed class NoOpKafkaProducer : IKafkaProducer
{
    /// <inheritdoc />
    public Task ProduceAsync<T>(string topic, string key, EventEnvelope<T> envelope, CancellationToken ct = default)
    {
        throw new InvalidOperationException(
            "Kafka is not configured (KAFKA_BOOTSTRAP_SERVERS empty). Outbox rows stay unpublished.");
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}
