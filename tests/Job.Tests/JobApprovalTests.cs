using Job.Core.Entities;
using SharedKernel;
using JobPosting = Job.Core.Entities.Job;

namespace Job.Tests;

/// <summary>
/// Domain tests for admin job approval/rejection workflow (PR3 — feat(admin): Slice 3).
/// Tests: ApprovePending, RejectPending, constructor initialStatus overload.
/// </summary>
public class JobApprovalTests
{
    private static JobPosting NewPendingJob() =>
        new("Backend Developer", "Build APIs", Guid.NewGuid(), "Da Nang", Guid.NewGuid(),
            initialStatus: JobStatus.Pending);

    private static JobPosting NewActiveJob() =>
        new("Backend Developer", "Build APIs", Guid.NewGuid(), "Da Nang", Guid.NewGuid());

    // ── Constructor initialStatus overload ────────────────────────────────────

    [Fact]
    public void Ctor_WithInitialStatus_Pending_SetsStatusPending()
    {
        var job = new JobPosting(
            "Dev", "Desc", Guid.NewGuid(), "Da Nang", Guid.NewGuid(),
            initialStatus: JobStatus.Pending);

        Assert.Equal(JobStatus.Pending, job.Status);
    }

    [Fact]
    public void Ctor_WithInitialStatus_Active_SetsStatusActive()
    {
        var job = new JobPosting(
            "Dev", "Desc", Guid.NewGuid(), "Da Nang", Guid.NewGuid(),
            initialStatus: JobStatus.Active);

        Assert.Equal(JobStatus.Active, job.Status);
    }

    [Fact]
    public void Ctor_WithoutInitialStatus_DefaultsToActive()
    {
        var job = new JobPosting("Dev", "Desc", Guid.NewGuid(), "Da Nang", Guid.NewGuid());

        Assert.Equal(JobStatus.Active, job.Status);
    }

    // ── ApprovePending ────────────────────────────────────────────────────────

    [Fact]
    public void ApprovePending_Success_SetsActiveAndApprovedFields()
    {
        var job = NewPendingJob();
        var adminId = Guid.NewGuid();

        var result = job.ApprovePending(adminId);

        Assert.True(result.IsSuccess);
        Assert.Equal(JobStatus.Active, job.Status);
        Assert.Equal(adminId, job.ApprovedBy);
        Assert.NotNull(job.ApprovedAt);
    }

    [Fact]
    public void ApprovePending_WhenAlreadyActive_ReturnsFailure()
    {
        var job = NewActiveJob();
        var adminId = Guid.NewGuid();

        var result = job.ApprovePending(adminId);

        Assert.False(result.IsSuccess);
        Assert.Contains("Pending", result.Error);
        Assert.Equal(JobStatus.Active, job.Status); // status unchanged
    }

    [Fact]
    public void ApprovePending_WhenClosed_ReturnsFailure()
    {
        var job = NewActiveJob();
        job.Close();
        var adminId = Guid.NewGuid();

        var result = job.ApprovePending(adminId);

        Assert.False(result.IsSuccess);
        Assert.Equal(JobStatus.Closed, job.Status); // status unchanged
    }

    [Fact]
    public void ApprovePending_WhenRejected_ReturnsFailure()
    {
        var job = NewPendingJob();
        job.RejectPending("Not suitable", Guid.NewGuid());

        var result = job.ApprovePending(Guid.NewGuid());

        Assert.False(result.IsSuccess);
        Assert.Equal(JobStatus.Rejected, job.Status); // status unchanged
    }

    // ── RejectPending ─────────────────────────────────────────────────────────

    [Fact]
    public void RejectPending_Success_SetsRejectedFieldsAndReason()
    {
        var job = NewPendingJob();
        var adminId = Guid.NewGuid();
        const string reason = "Job title is misleading.";

        var result = job.RejectPending(reason, adminId);

        Assert.True(result.IsSuccess);
        Assert.Equal(JobStatus.Rejected, job.Status);
        Assert.Equal(reason.Trim(), job.RejectionReason);
        Assert.Equal(adminId, job.RejectedBy);
        Assert.NotNull(job.RejectedAt);
    }

    [Fact]
    public void RejectPending_TrimsReason()
    {
        var job = NewPendingJob();
        var result = job.RejectPending("  too vague  ", Guid.NewGuid());

        Assert.True(result.IsSuccess);
        Assert.Equal("too vague", job.RejectionReason);
    }

    [Fact]
    public void RejectPending_EmptyReason_ReturnsFailure()
    {
        var job = NewPendingJob();

        var result = job.RejectPending("", Guid.NewGuid());

        Assert.False(result.IsSuccess);
        Assert.Contains("required", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(JobStatus.Pending, job.Status); // status unchanged
    }

    [Fact]
    public void RejectPending_WhiteSpaceReason_ReturnsFailure()
    {
        var job = NewPendingJob();

        var result = job.RejectPending("   ", Guid.NewGuid());

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void RejectPending_ReasonTooLong_ReturnsFailure()
    {
        var job = NewPendingJob();
        var longReason = new string('x', 501);

        var result = job.RejectPending(longReason, Guid.NewGuid());

        Assert.False(result.IsSuccess);
        Assert.Contains("500", result.Error);
        Assert.Equal(JobStatus.Pending, job.Status); // status unchanged
    }

    [Fact]
    public void RejectPending_ReasonExactly500Chars_Succeeds()
    {
        var job = NewPendingJob();
        var reason = new string('x', 500);

        var result = job.RejectPending(reason, Guid.NewGuid());

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void RejectPending_WhenAlreadyActive_ReturnsFailure()
    {
        var job = NewActiveJob();

        var result = job.RejectPending("Not suitable", Guid.NewGuid());

        Assert.False(result.IsSuccess);
        Assert.Equal(JobStatus.Active, job.Status); // status unchanged
    }

    [Fact]
    public void RejectPending_WhenNotPending_DoesNotModifyRejectionFields()
    {
        var job = NewActiveJob();

        job.RejectPending("Not suitable", Guid.NewGuid());

        Assert.Null(job.RejectionReason);
        Assert.Null(job.RejectedBy);
        Assert.Null(job.RejectedAt);
    }
}
