namespace Job.Core.Entities;

/// <summary>
/// Job lifecycle statuses.
/// Stored as string in DB (HasConversion&lt;string&gt;()) — adding new values requires no ALTER TABLE.
/// </summary>
public enum JobStatus
{
    Active,
    Closed,
    Deleted,
    /// <summary>Awaiting admin approval (admin-approval feature flag enabled).</summary>
    Pending,
    /// <summary>Rejected by admin — recruiter can see reason via RejectionReason field.</summary>
    Rejected
}
