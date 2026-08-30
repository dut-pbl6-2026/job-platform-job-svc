using SharedKernel;

namespace Job.Core.Entities;

public class SavedJob : Entity
{
    public Guid UserId { get; private set; }
    public Guid JobId { get; private set; }
    public Job? Job { get; private set; }

    // A3: SavedAt maps to Entity.CreatedAt — no extra column.
    // SRS 3.3.4 data model specifies saved_at; we surface it via this property
    // so the API can return it without a redundant DB column.
    public DateTime SavedAt => CreatedAt;

    private SavedJob() { }

    public SavedJob(Guid userId, Guid jobId)
    {
        UserId = userId;
        JobId = jobId;
    }
}
