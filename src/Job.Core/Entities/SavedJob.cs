using SharedKernel;

namespace Job.Core.Entities;

public class SavedJob : Entity
{
    public Guid UserId { get; private set; }
    public Guid JobId { get; private set; }
    public Job? Job { get; private set; }
    public DateTime SavedAt { get; private set; } = DateTime.UtcNow;

    private SavedJob() { }

    public SavedJob(Guid userId, Guid jobId)
    {
        UserId = userId;
        JobId = jobId;
    }
}
