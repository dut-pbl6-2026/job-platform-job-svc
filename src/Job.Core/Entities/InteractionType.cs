namespace Job.Core.Entities;

/// <summary>
/// Kind of user → job interaction kept in the recommendation read model (PBL6-10).
/// Stored as string (max 32). Saved jobs are read from <see cref="SavedJob"/>, not duplicated here.
/// </summary>
public enum InteractionType
{
    /// <summary>The user applied to the job (app-svc <c>application.*</c> events / backfill).</summary>
    Applied,
}
