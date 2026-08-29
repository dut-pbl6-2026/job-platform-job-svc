using JobPosting = Job.Core.Entities.Job;

namespace Job.Tests;

public class JobEntityTests
{
    [Fact]
    public void SoftDeleteMarksJobAsDeleted()
    {
        var job = NewJob();

        job.SoftDelete();

        Assert.Equal("Deleted", job.Status);
    }

    [Fact]
    public void IncrementViewIncreasesViewCount()
    {
        var job = NewJob();

        job.IncrementView();
        job.IncrementView();

        Assert.Equal(2, job.ViewCount);
    }

    private static JobPosting NewJob()
    {
        return new JobPosting(
            "Backend Developer",
            "Build and maintain services",
            Guid.NewGuid(),
            "Da Nang",
            Guid.NewGuid());
    }
}
