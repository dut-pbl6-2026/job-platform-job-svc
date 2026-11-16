using Job.Core.Entities;
using JobPosting = Job.Core.Entities.Job;

namespace Job.Tests;

/// <summary>
/// Domain tests for web-admin moderation: takedown with a required reason and restore.
/// There is no approve/reject step: jobs are created Active by the API.
/// </summary>
public class JobModerationTests
{
    private static JobPosting NewActiveJob() =>
        new("Backend Developer", "Build APIs", Guid.NewGuid(), "Da Nang", Guid.NewGuid());

    [Fact]
    public void Ctor_DefaultsToActive()
    {
        Assert.Equal(JobStatus.Active, NewActiveJob().Status);
    }

    [Fact]
    public void Ctor_WithInitialStatus_StillSupportsLegacySeeding()
    {
        var job = new JobPosting(
            "Dev", "Desc", Guid.NewGuid(), "Da Nang", Guid.NewGuid(),
            initialStatus: JobStatus.Closed);

        Assert.Equal(JobStatus.Closed, job.Status);
    }

    // ── Takedown ──────────────────────────────────────────────────────────────

    [Fact]
    public void Takedown_SetsDeletedAndAuditFields()
    {
        var job = NewActiveJob();
        var adminId = Guid.NewGuid();

        var result = job.Takedown("  Misleading salary  ", adminId);

        Assert.True(result.IsSuccess);
        Assert.Equal(JobStatus.Deleted, job.Status);
        Assert.Equal("Misleading salary", job.ModerationReason);
        Assert.Equal(adminId, job.ModeratedBy);
        Assert.NotNull(job.ModeratedAt);
    }

    [Fact]
    public void Takedown_ClosedJob_AlsoHidesIt()
    {
        var job = NewActiveJob();
        job.Close();

        var result = job.Takedown("Company reported", Guid.NewGuid());

        Assert.True(result.IsSuccess);
        Assert.Equal(JobStatus.Deleted, job.Status);
    }

    [Fact]
    public void Takedown_EmptyReason_ReturnsFailureAndKeepsState()
    {
        var job = NewActiveJob();

        var result = job.Takedown("   ", Guid.NewGuid());

        Assert.False(result.IsSuccess);
        Assert.Contains("required", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(JobStatus.Active, job.Status);
        Assert.Null(job.ModerationReason);
        Assert.Null(job.ModeratedBy);
        Assert.Null(job.ModeratedAt);
    }

    [Fact]
    public void Takedown_ReasonTooLong_ReturnsFailureAndKeepsState()
    {
        var job = NewActiveJob();

        var result = job.Takedown(new string('x', JobPosting.ModerationReasonMaxLength + 1), Guid.NewGuid());

        Assert.False(result.IsSuccess);
        Assert.Contains("500", result.Error);
        Assert.Equal(JobStatus.Active, job.Status);
        Assert.Null(job.ModerationReason);
    }

    [Fact]
    public void Takedown_ReasonExactlyMaxLength_Succeeds()
    {
        var job = NewActiveJob();

        var result = job.Takedown(new string('x', JobPosting.ModerationReasonMaxLength), Guid.NewGuid());

        Assert.True(result.IsSuccess);
        Assert.Equal(JobPosting.ModerationReasonMaxLength, job.ModerationReason!.Length);
    }

    // ── Restore ───────────────────────────────────────────────────────────────

    [Fact]
    public void Restore_TakenDownJob_ReactivatesAndClearsAudit()
    {
        var job = NewActiveJob();
        job.Takedown("Spam", Guid.NewGuid());

        var result = job.Restore();

        Assert.True(result.IsSuccess);
        Assert.Equal(JobStatus.Active, job.Status);
        Assert.Null(job.ModerationReason);
        Assert.Null(job.ModeratedBy);
        Assert.Null(job.ModeratedAt);
    }

    [Theory]
    [InlineData(JobStatus.Active)]
    [InlineData(JobStatus.Closed)]
    public void Restore_NonDeletedJob_ReturnsFailure(JobStatus status)
    {
        var job = new JobPosting("Dev", "Desc", Guid.NewGuid(), "Da Nang", Guid.NewGuid(), initialStatus: status);

        var result = job.Restore();

        Assert.False(result.IsSuccess);
        Assert.Equal(status, job.Status);
    }

    [Fact]
    public void Restore_JobDeletedByItsRecruiter_ReturnsFailure()
    {
        // A recruiter DELETE is a soft delete without a moderation record: not restorable by an admin.
        var job = NewActiveJob();
        job.SoftDelete();

        var result = job.Restore();

        Assert.False(result.IsSuccess);
        Assert.Equal(JobStatus.Deleted, job.Status);
    }
}
