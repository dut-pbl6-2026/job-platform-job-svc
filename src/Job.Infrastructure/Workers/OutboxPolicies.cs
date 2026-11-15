using Job.Core.Entities;

namespace Job.Infrastructure.Workers;

/// <summary>
/// Retry decision for a failed outbox publish: bounded exponential backoff, never give-up.
/// </summary>
public static class OutboxRetryPolicy
{
    /// <summary>Delay after the first failure.</summary>
    public static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(5);

    /// <summary>Upper bound on the delay between attempts.</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(5);

    /// <summary>From this many failed attempts on, each failure is logged at Error with the outbox row id.</summary>
    public const int ErrorLogThreshold = 5;

    /// <summary>Delay before the next attempt, given the failure count after this failure (1-based).</summary>
    public static TimeSpan DelayAfterFailure(int retryCount)
    {
        var exponent = Math.Min(Math.Max(retryCount - 1, 0), 30);
        var seconds = Math.Min(MaxDelay.TotalSeconds, BaseDelay.TotalSeconds * Math.Pow(2, exponent));
        return TimeSpan.FromSeconds(seconds);
    }
}

/// <summary>
/// Claim decision for one publisher cycle, applied to unpublished rows in publish order.
/// The relational path applies the same rules in SQL (see <see cref="OutboxPublisherService"/>).
/// </summary>
public static class OutboxClaimPolicy
{
    /// <summary>
    /// Selects the rows this cycle may publish. A row is skipped when it is not yet due, or when an
    /// earlier unpublished row of the same aggregate exists (even if that row is not due): this keeps
    /// per-job ordering when several replicas publish at once.
    /// </summary>
    /// <param name="unpublishedInOrder">Unpublished rows ordered by CreatedAt, Id.</param>
    public static List<OutboxEvent> SelectClaimable(
        IEnumerable<OutboxEvent> unpublishedInOrder, DateTime now, int batchSize)
    {
        var selected = new List<OutboxEvent>();
        var blockedAggregates = new HashSet<string>(StringComparer.Ordinal);

        foreach (var outboxEvent in unpublishedInOrder)
        {
            var aggregate = outboxEvent.AggregateId;
            var blocked = aggregate is not null && blockedAggregates.Contains(aggregate);

            if (!blocked && outboxEvent.NextAttemptAt <= now)
            {
                selected.Add(outboxEvent);
                if (selected.Count == batchSize)
                    break;
            }

            if (aggregate is not null)
                blockedAggregates.Add(aggregate);
        }

        return selected;
    }
}
