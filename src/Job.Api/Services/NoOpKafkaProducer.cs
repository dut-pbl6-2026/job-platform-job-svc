using SharedKernel.Events;
using SharedKernel.Kafka;

namespace Job.Api.Services;

/// <summary>
/// No-op Kafka producer used when <c>KAFKA_BOOTSTRAP_SERVERS</c> is not set.
/// All publish calls are silently skipped — <see cref="KafkaJobEventPublisher"/>
/// will immediately fall through to the HTTP fallback path.
/// </summary>
public sealed class NoOpKafkaProducer : IKafkaProducer
{
    /// <inheritdoc />
    public Task ProduceAsync<T>(string topic, string key, EventEnvelope<T> envelope, CancellationToken ct = default)
    {
        // Throw so the circuit-breaker in KafkaJobEventPublisher detects failure
        // and switches to HTTP fallback immediately.
        throw new InvalidOperationException(
            "Kafka is not configured (KAFKA_BOOTSTRAP_SERVERS empty). " +
            "This is expected — the publisher will fall back to HTTP sync.");
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}
