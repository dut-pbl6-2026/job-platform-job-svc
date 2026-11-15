using SharedKernel;

namespace Job.Core.Entities;

/// <summary>
/// Single-use recruiter invite code (SRS AUTH-01-09). Only the SHA-256 hash of the code is stored.
/// Issued by the company creator (<see cref="Company.CreatedBy"/>); redeemed once by auth-svc.
/// </summary>
public class InviteCode : Entity
{
    /// <summary>Revoke reason set when the Company admin changes (AUTH-01-09, AUTH-01-14).</summary>
    public const string RevokeReasonAdminChanged = "ADMIN_CHANGED";

    public Guid CompanyId { get; private set; }
    public string CodeHash { get; private set; } = "";

    /// <summary>Normalised (trimmed, lower-case) address the code is bound to (AUTH-01-15).</summary>
    public string Email { get; private set; } = "";
    public InviteCodeStatus Status { get; private set; } = InviteCodeStatus.Active;
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? UsedAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public string? RevokeReason { get; private set; }

    private InviteCode() { }

    public InviteCode(
        Guid companyId,
        string codeHash,
        string email,
        Guid createdBy,
        DateTimeOffset issuedAt,
        TimeSpan ttl)
    {
        if (companyId == Guid.Empty)
            throw new ArgumentException("CompanyId is required.", nameof(companyId));
        if (createdBy == Guid.Empty)
            throw new ArgumentException("CreatedBy is required.", nameof(createdBy));
        if (string.IsNullOrWhiteSpace(codeHash) || codeHash.Length != 64)
            throw new ArgumentException("CodeHash must be a SHA-256 hex digest.", nameof(codeHash));
        if (string.IsNullOrWhiteSpace(email))
            throw new ArgumentException("Email is required.", nameof(email));
        if (ttl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ttl), "TTL must be positive.");

        CompanyId = companyId;
        CodeHash = codeHash;
        Email = email;
        CreatedBy = createdBy;
        ExpiresAt = issuedAt + ttl;
    }

    /// <summary>True when the code is unused, not revoked and not expired at <paramref name="now"/>.</summary>
    public bool IsRedeemable(string email, DateTimeOffset now) =>
        Status == InviteCodeStatus.Active && UsedAt is null && ExpiresAt > now
        && string.Equals(Email, email, StringComparison.Ordinal);

    /// <summary>
    /// Revokes an unused code. Used codes are left as they are (AUTH-01-09).
    /// Returns false when nothing changed.
    /// </summary>
    public bool RevokeUnused(DateTimeOffset at, string reason)
    {
        if (Status != InviteCodeStatus.Active || UsedAt is not null)
            return false;
        Status = InviteCodeStatus.Revoked;
        RevokedAt = at;
        RevokeReason = reason;
        return true;
    }

    /// <summary>Marks the code used. Throws if it was already used.</summary>
    public void MarkUsed(DateTimeOffset usedAt)
    {
        if (UsedAt is not null)
            throw new InvalidOperationException("Invite code is already used.");
        UsedAt = usedAt;
    }

    /// <summary>Clears the used marker after a failed registration (auth-svc release call).</summary>
    public void ClearUsed() => UsedAt = null;
}

/// <summary>Invite code status, stored as text (AUTH-01-09).</summary>
public enum InviteCodeStatus
{
    Active,
    Revoked,
}
