using SharedKernel;

namespace Job.Core.Entities;

/// <summary>
/// Transactional outbox event for reliable Kafka delivery (feat(admin): Slice 4 — R-1 dual-write fix).
/// INSERT into OutboxEvents within the same DB transaction as the domain change;
/// <see cref="Job.Infrastructure.Workers.OutboxPublisherService"/> polls and publishes to Kafka.
/// </summary>
public class OutboxEvent : Entity
{
    /// <summary>Kafka event type, e.g. "job.updated".</summary>
    public string EventType { get; init; } = "";

    /// <summary>JSON-serialized event payload (e.g. <see cref="SharedKernel.Events.JobUpdatedEvent"/>).</summary>
    public string Payload { get; init; } = "";

    /// <summary>Aggregate id (Job.Id.ToString()) for idempotency and ordering.</summary>
    public string? AggregateId { get; init; }

    /// <summary>True once the event has been successfully published to Kafka.</summary>
    public bool IsPublished { get; private set; }

    /// <summary>UTC timestamp when the event was published.</summary>
    public DateTime? PublishedAt { get; private set; }

    /// <summary>Number of failed publish attempts (incremented before each retry).</summary>
    public int RetryCount { get; private set; }

    // Required by EF Core
    private OutboxEvent() { }

    public OutboxEvent(string eventType, string payload, string? aggregateId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType, nameof(eventType));
        ArgumentException.ThrowIfNullOrWhiteSpace(payload, nameof(payload));
        EventType = eventType;
        Payload = payload;
        AggregateId = aggregateId;
    }

    /// <summary>Marks the event as successfully published to Kafka.</summary>
    public void MarkPublished()
    {
        IsPublished = true;
        PublishedAt = DateTime.UtcNow;
    }

    /// <summary>Increments the retry counter before each failed publish attempt.</summary>
    public void IncrementRetry() => RetryCount++;
}
