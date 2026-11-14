using SharedKernel;

namespace Job.Core.Entities;

/// <summary>
/// Recommendation read model row (PBL6-10): user <see cref="UserId"/> interacted with job
/// <see cref="JobId"/>. Built from app-svc events and backfill, so <see cref="JobId"/> has no
/// foreign key — jobs are filtered (Active only) when read. Unique on (UserId, JobId, Type).
/// </summary>
public class JobInteraction : Entity
{
    public Guid UserId { get; private set; }
    public Guid JobId { get; private set; }
    public InteractionType Type { get; private set; }

    /// <summary>Id of the source record in the owning service (ApplicationId for <see cref="InteractionType.Applied"/>).</summary>
    public Guid? SourceId { get; private set; }

    /// <summary>When the interaction happened (UTC), from the source event.</summary>
    public DateTime OccurredAt { get; private set; }

    private JobInteraction() { }

    public JobInteraction(Guid userId, Guid jobId, InteractionType type, Guid? sourceId, DateTime occurredAt)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("UserId is required.", nameof(userId));
        if (jobId == Guid.Empty)
            throw new ArgumentException("JobId is required.", nameof(jobId));

        UserId = userId;
        JobId = jobId;
        Type = type;
        SourceId = sourceId == Guid.Empty ? null : sourceId;
        OccurredAt = AsUtc(occurredAt);
    }

    /// <summary>Unspecified kind is treated as UTC (event payloads are UTC); local times are converted.</summary>
    public static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
