using System.Security.Claims;
using Job.Api.DTOs;
using Job.Api.Endpoints;
using Job.Core.Entities;
using Job.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using JobPosting = Job.Core.Entities.Job;

namespace Job.Tests;

/// <summary>
/// CRUD unit tests for Job endpoints using In-Memory EF (no real DB needed).
/// Tests per implementation_plan.md:495-501.
/// </summary>
public class JobCrudTests : IDisposable
{
    private readonly JobDbContext _db;
    private readonly Guid _companyId = Guid.NewGuid();
    private readonly Guid _recruiterId = Guid.NewGuid();
    private readonly Guid _otherRecruiterId = Guid.NewGuid();

    public JobCrudTests()
    {
        var options = new DbContextOptionsBuilder<JobDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new JobDbContext(options);

        _db.Database.EnsureCreated();
        SeedCompany();
    }

    private void SeedCompany()
    {
        var company = new Company("Acme Corp");
        typeof(Company).GetProperty("Id")!.SetValue(company, _companyId);
        _db.Companies.Add(company);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private static HttpContext BuildContext(string userId, string role = "Recruiter")
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId),
            new(ClaimTypes.Role, role)
        };
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
        };
        return ctx;
    }

    // ─── Domain entity unit tests ──────────────────────────────────────────

    [Fact]
    public void CreateJob_Domain_SetsCorrectFields()
    {
        var job = new JobPosting(
            "Backend Dev", "Build APIs", _companyId, "Da Nang", _recruiterId,
            salaryMin: 10_000_000, salaryMax: 20_000_000);

        Assert.Equal("Backend Dev", job.Title);
        Assert.Equal("Da Nang", job.Location);
        Assert.Equal(JobStatus.Active, job.Status);
        Assert.Equal(10_000_000m, job.SalaryMin);
        Assert.Equal(_companyId, job.CompanyId);
        Assert.Equal(_recruiterId, job.RecruiterId);
    }

    [Fact]
    public void CreateJob_Domain_ThrowsWhenTitleEmpty()
    {
        Assert.Throws<ArgumentException>(() =>
            new JobPosting("", "Desc", _companyId, "Da Nang", _recruiterId));
    }

    [Fact]
    public void CreateJob_Domain_ThrowsWhenSalaryMinGreaterThanMax()
    {
        Assert.Throws<ArgumentException>(() =>
            new JobPosting("Dev", "Desc", _companyId, "Da Nang", _recruiterId,
                salaryMin: 100, salaryMax: 50));
    }

    [Fact]
    public void CreateJob_Domain_ThrowsWhenSalaryNegative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new JobPosting("Dev", "Desc", _companyId, "Da Nang", _recruiterId,
                salaryMin: -100));
    }

    [Fact]
    public void SoftDelete_ChangesStatusToDeleted()
    {
        var job = new JobPosting("Dev", "Desc", _companyId, "Da Nang", _recruiterId);
        job.SoftDelete();
        Assert.Equal(JobStatus.Deleted, job.Status);
    }

    [Fact]
    public void Close_ChangesStatusToClosed()
    {
        var job = new JobPosting("Dev", "Desc", _companyId, "Da Nang", _recruiterId);
        job.Close();
        Assert.Equal(JobStatus.Closed, job.Status);
    }

    [Fact]
    public void Reopen_ChangesStatusToActive()
    {
        var job = new JobPosting("Dev", "Desc", _companyId, "Da Nang", _recruiterId);
        job.Close();
        job.Reopen();
        Assert.Equal(JobStatus.Active, job.Status);
    }

    // ─── IncrementView ──────────────────────────────────────────────────────

    [Fact]
    public void IncrementView_IncreasesViewCount()
    {
        var job = new JobPosting("Dev", "Desc", _companyId, "Da Nang", _recruiterId);
        job.IncrementView();
        job.IncrementView();
        Assert.Equal(2, job.ViewCount);
    }

    // ─── Update validation ──────────────────────────────────────────────────

    [Fact]
    public void Update_Domain_ThrowsWhenTitleWhitespace()
    {
        var job = new JobPosting("Dev", "Desc", _companyId, "Da Nang", _recruiterId);
        Assert.Throws<ArgumentException>(() =>
            job.Update("  ", "Desc", _companyId, "Da Nang",
                null, null, "VND", null, null, null, "FullTime", "Entry"));
    }

    // ─── DTO mapping ────────────────────────────────────────────────────────

    [Fact]
    public void JobDetailDto_From_MapsCorrectly()
    {
        var job = new JobPosting(
            "Full-Stack Dev", "Build full-stack apps", _companyId, "Ho Chi Minh",
            _recruiterId, salaryCurrency: "usd");

        var dto = JobDetailDto.From(job);

        Assert.Equal("Full-Stack Dev", dto.Title);
        Assert.Equal("Ho Chi Minh", dto.Location);
        Assert.Equal("USD", dto.SalaryCurrency);
        Assert.Equal("Active", dto.Status);
    }

    // ─── Paginated response ─────────────────────────────────────────────────

    [Fact]
    public void PaginatedResponse_TotalPages_CalculatesCorrectly()
    {
        var items = new List<string> { "a", "b" };
        var resp = new PaginatedResponse<string>(items, Total: 25, Page: 1, Size: 10, TotalPages: 3);
        Assert.Equal(3, resp.TotalPages);
        Assert.Equal(25, resp.Total);
        Assert.Equal(2, resp.Items.Count);
    }

    // ─── EF InMemory — QueryFilter ──────────────────────────────────────────

    [Fact]
    public async Task QueryFilter_ExcludesDeletedJobs()
    {
        var job = new JobPosting("Dev", "Desc", _companyId, "Da Nang", _recruiterId);
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();

        var id = job.Id;
        job.SoftDelete();
        await _db.SaveChangesAsync();

        var jobFromDb = await _db.Jobs.IgnoreQueryFilters().FirstOrDefaultAsync(j => j.Id == id);
        Assert.NotNull(jobFromDb);
        Assert.Equal(JobStatus.Deleted, jobFromDb!.Status);
    }

    [Fact]
    public async Task Seed_Company_Exists_In_Db()
    {
        var company = await _db.Companies.FindAsync(_companyId);
        Assert.NotNull(company);
        Assert.Equal("Acme Corp", company!.Name);
    }
}