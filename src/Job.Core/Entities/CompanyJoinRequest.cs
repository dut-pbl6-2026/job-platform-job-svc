using SharedKernel;

namespace Job.Core.Entities;

/// <summary>Stored status of a join request (SRS AUTH-01-12 to AUTH-01-14).</summary>
public enum CompanyJoinRequestStatus
{
    Pending,
    Approved,
    Rejected,
    Expired,
    Escalated,
    SupportApproved,
    SupportRejected,
}

/// <summary>
/// A work-email sign-up whose domain belongs to a verified Company (SRS AUTH-01-12).
/// One request per account (<see cref="UserId"/> is unique): a rejection is final except through
/// escalation to support after expiry (AUTH-01-14).
/// </summary>
public class CompanyJoinRequest : Entity
{
    public const int DecisionReasonMaxLength = 500;

    public Guid CompanyId { get; private set; }
    public Guid UserId { get; private set; }
    public CompanyJoinRequestStatus Status { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public Guid? DecidedBy { get; private set; }
    public DateTimeOffset? DecidedAt { get; private set; }
    public string? DecisionReason { get; private set; }

    private CompanyJoinRequest() { }

    public CompanyJoinRequest(Guid companyId, Guid userId, DateTimeOffset createdAt, TimeSpan waitingPeriod)
    {
        if (companyId == Guid.Empty)
            throw new ArgumentException("CompanyId is required.", nameof(companyId));
        if (userId == Guid.Empty)
            throw new ArgumentException("UserId is required.", nameof(userId));
        if (waitingPeriod <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(waitingPeriod), "Waiting period must be positive.");

        CompanyId = companyId;
        UserId = userId;
        Status = CompanyJoinRequestStatus.Pending;
        ExpiresAt = createdAt + waitingPeriod;
        CreatedAt = createdAt.UtcDateTime;
        UpdatedAt = CreatedAt;
    }

    /// <summary>
    /// Effective status at <paramref name="now"/> (AUTH-01-13). A Pending, Rejected or Expired request
    /// whose waiting period has passed reads as Expired. Terminal and escalated states never change.
    /// </summary>
    public CompanyJoinRequestStatus EffectiveStatus(DateTimeOffset now) =>
        IsExpiryEligible() && now >= ExpiresAt ? CompanyJoinRequestStatus.Expired : Status;

    /// <summary>True when the stored status may be turned into Expired by the daily job or the read path.</summary>
    public bool IsExpiryEligible() =>
        Status is CompanyJoinRequestStatus.Pending
            or CompanyJoinRequestStatus.Rejected
            or CompanyJoinRequestStatus.Expired;

    public bool IsPending(DateTimeOffset now) => EffectiveStatus(now) == CompanyJoinRequestStatus.Pending;

    /// <summary>Admin approval. Only a Pending, unexpired request can be approved.</summary>
    public void Approve(Guid decidedBy, DateTimeOffset now)
    {
        EnsureDecidable(now);
        Status = CompanyJoinRequestStatus.Approved;
        Decide(decidedBy, now, reason: null);
    }

    /// <summary>Admin rejection. Final for the requester unless escalated after expiry.</summary>
    public void Reject(Guid decidedBy, DateTimeOffset now, string? reason)
    {
        EnsureDecidable(now);
        Status = CompanyJoinRequestStatus.Rejected;
        Decide(decidedBy, now, reason);
    }

    /// <summary>Requester escalation. Allowed once, only when the effective status is Expired.</summary>
    public void Escalate(DateTimeOffset now)
    {
        if (EffectiveStatus(now) != CompanyJoinRequestStatus.Expired)
            throw new InvalidOperationException("Only an expired join request can be escalated.");
        Status = CompanyJoinRequestStatus.Escalated;
        Touch();
    }

    /// <summary>Stores Expired for a Pending request whose waiting period has passed (daily job).</summary>
    public void MarkExpired()
    {
        if (Status != CompanyJoinRequestStatus.Pending)
            throw new InvalidOperationException("Only a pending join request is marked expired.");
        Status = CompanyJoinRequestStatus.Expired;
        Touch();
    }

    /// <summary>Support decision on an escalated request. The caller has already linked the account.</summary>
    public void DecideBySupport(bool approve, string? reason, DateTimeOffset now)
    {
        if (Status != CompanyJoinRequestStatus.Escalated)
            throw new InvalidOperationException("Only an escalated join request can be decided by support.");
        Status = approve ? CompanyJoinRequestStatus.SupportApproved : CompanyJoinRequestStatus.SupportRejected;
        DecidedAt = now;
        DecisionReason = Truncate(reason);
        Touch();
    }

    private void EnsureDecidable(DateTimeOffset now)
    {
        if (EffectiveStatus(now) != CompanyJoinRequestStatus.Pending)
            throw new InvalidOperationException("Join request is not pending.");
    }

    private void Decide(Guid decidedBy, DateTimeOffset now, string? reason)
    {
        DecidedBy = decidedBy;
        DecidedAt = now;
        DecisionReason = Truncate(reason);
        Touch();
    }

    private static string? Truncate(string? reason)
    {
        var trimmed = reason?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;
        return trimmed.Length <= DecisionReasonMaxLength ? trimmed : trimmed[..DecisionReasonMaxLength];
    }
}
