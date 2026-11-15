using SharedKernel;

namespace Job.Core.Entities;

/// <summary>
/// Single-use recruiter invite code (SRS AUTH-01-09). Only the SHA-256 hash of the code is stored.
/// Issued by the company creator (<see cref="Company.CreatedBy"/>); redeemed once by auth-svc.
/// </summary>
public class InviteCode : Entity
{
    public Guid CompanyId { get; private set; }
    public string CodeHash { get; private set; } = "";
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? UsedAt { get; private set; }

    private InviteCode() { }

    public InviteCode(Guid companyId, string codeHash, Guid createdBy, DateTimeOffset issuedAt, TimeSpan ttl)
    {
        if (companyId == Guid.Empty)
            throw new ArgumentException("CompanyId is required.", nameof(companyId));
        if (createdBy == Guid.Empty)
            throw new ArgumentException("CreatedBy is required.", nameof(createdBy));
        if (string.IsNullOrWhiteSpace(codeHash) || codeHash.Length != 64)
            throw new ArgumentException("CodeHash must be a SHA-256 hex digest.", nameof(codeHash));
        if (ttl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ttl), "TTL must be positive.");

        CompanyId = companyId;
        CodeHash = codeHash;
        CreatedBy = createdBy;
        ExpiresAt = issuedAt + ttl;
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
