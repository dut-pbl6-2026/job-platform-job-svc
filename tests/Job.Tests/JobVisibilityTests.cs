using Job.Core.Entities;
using Job.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using JobPosting = Job.Core.Entities.Job;

namespace Job.Tests;

/// <summary>
/// The default query filter on Jobs is what public reads see. Deleted, Pending and Rejected must be
/// hidden; Active and Closed must be visible. Internal and recruiter-owner paths opt out with IgnoreQueryFilters.
/// </summary>
public class JobVisibilityTests : IDisposable
{
    private readonly JobDbContext _db;
    private readonly Guid _companyId = Guid.NewGuid();
    private readonly Guid _recruiterId = Guid.NewGuid();

    public JobVisibilityTests()
    {
        var options = new DbContextOptionsBuilder<JobDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new JobDbContext(options);
        foreach (var status in Enum.GetValues<JobStatus>())
            _db.Jobs.Add(new JobPosting($"{status} job", "Desc", _companyId, "Da Nang", _recruiterId, status));
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task PublicQuery_HidesDeletedPendingAndRejected()
    {
        var statuses = await _db.Jobs.Select(j => j.Status).ToListAsync();

        Assert.DoesNotContain(JobStatus.Deleted, statuses);
        Assert.DoesNotContain(JobStatus.Pending, statuses);
        Assert.DoesNotContain(JobStatus.Rejected, statuses);
    }

    [Fact]
    public async Task PublicQuery_KeepsActiveAndClosed()
    {
        var statuses = await _db.Jobs.Select(j => j.Status).ToListAsync();

        Assert.Contains(JobStatus.Active, statuses);
        Assert.Contains(JobStatus.Closed, statuses);
        Assert.Equal(2, statuses.Count);
    }

    [Fact]
    public async Task IgnoreQueryFilters_SeesEveryStatus()
    {
        var statuses = await _db.Jobs.IgnoreQueryFilters().Select(j => j.Status).ToListAsync();

        Assert.Equal(Enum.GetValues<JobStatus>().Length, statuses.Count);
        Assert.Equal(Enum.GetValues<JobStatus>().Order(), statuses.Order());
    }
}
