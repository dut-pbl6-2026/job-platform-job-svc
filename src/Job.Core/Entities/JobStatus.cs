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

    // ── Legacy values from the removed admin pre-approval flow ────────────────
    // Jobs publish instantly now; new code never assigns these. They stay so internal stats and
    // admin detail can still render an unmigrated row, and so the contract with auth-svc's
    // dashboard (GET /api/internal/stats) keeps its fields. Migration ReplaceApprovalWithModeration
    // converts Pending -&gt; Active and Rejected -&gt; Closed.
    /// <summary>Legacy: awaiting admin approval. Converted to Active by migration.</summary>
    Pending,
    /// <summary>Legacy: rejected by admin. Converted to Closed by migration.</summary>
    Rejected
}
