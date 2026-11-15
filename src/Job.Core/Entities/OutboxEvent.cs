using SharedKernel;

namespace Job.Core.Entities;

/// <summary>
/// Transactional outbox event for reliable Kafka delivery.
/// INSERT into OutboxEvents within the same DB transaction as the domain change;
/// <see cref="Job.Infrastructure.Workers.OutboxPublisherService"/> polls and publishes to Kafka.
/// Rows are never dropped: a failing row is retried with exponential backoff until it is published.
/// </summary>
public class OutboxEvent : Entity
{
    /// <summary>Kafka event type, e.g. "job.updated".</summary>
    public string EventType { get; init; } = "";

    /// <summary>JSON-serialized event payload (e.g. <see cref="SharedKernel.Events.JobUpdatedEvent"/>).</summary>
    public string Payload { get; init; } = "";

    /// <summary>Aggregate id (Job.Id.ToString()) for idempotency and ordering.</summary>
    public string? AggregateId { get; init; }

    /// <summary>True once the event has been acknowledged by Kafka.</summary>
    public bool IsPublished { get; private set; }

    /// <summary>UTC timestamp when the event was published.</summary>
    public DateTime? PublishedAt { get; private set; }

    /// <summary>Number of failed publish attempts.</summary>
    public int RetryCount { get; private set; }

    /// <summary>UTC time before which the publisher must not try this row again.</summary>
    public DateTime NextAttemptAt { get; private set; }

    // Required by EF Core
    private OutboxEvent() { }

    public OutboxEvent(string eventType, string payload, string? aggregateId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType, nameof(eventType));
        ArgumentException.ThrowIfNullOrWhiteSpace(payload, nameof(payload));
        EventType = eventType;
        Payload = payload;
        AggregateId = aggregateId;
        NextAttemptAt = DateTime.UtcNow;
    }

    /// <summary>Marks the event as acknowledged by Kafka.</summary>
    public void MarkPublished()
    {
        IsPublished = true;
        PublishedAt = DateTime.UtcNow;
    }

    /// <summary>Records a failed publish attempt and schedules the next one.</summary>
    public void RecordFailure(DateTime nextAttemptAt)
    {
        RetryCount++;
        NextAttemptAt = nextAttemptAt;
    }
}
