using System.Reflection;
using System.Security.Claims;
using Job.Api.DTOs;
using Job.Api.Endpoints;
using Job.Core.Entities;
using Job.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SharedKernel.Events;
using JobPosting = Job.Core.Entities.Job;

namespace Job.Tests;

/// <summary>Recruiter job create/update must bind the job only to a company the caller created; update and delete only apply to jobs the caller owns.</summary>
public sealed class JobCompanyOwnershipTests : IDisposable
{
    private readonly JobDbContext _db;
    private readonly Guid _recruiterId = Guid.NewGuid();
    private readonly Guid _otherRecruiterId = Guid.NewGuid();
    private readonly Guid _ownCompanyId = Guid.NewGuid();
    private readonly Guid _otherCompanyId = Guid.NewGuid();
    private readonly Guid _secondOwnCompanyId = Guid.NewGuid();
    private readonly IConfiguration _configuration = new ConfigurationBuilder().Build();

    public JobCompanyOwnershipTests()
    {
        var options = new DbContextOptionsBuilder<JobDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new JobDbContext(options);

        AddCompany(_ownCompanyId, "Own Corp", _recruiterId);
        AddCompany(_secondOwnCompanyId, "Second Own Corp", _recruiterId);
        AddCompany(_otherCompanyId, "Other Corp", _otherRecruiterId);
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    [Fact]
    public async Task CreateJob_ForCompanyOwnedByAnotherRecruiter_Returns403AndSavesNothing()
    {
        var dto = CreateDto(_otherCompanyId);

        var result = await Invoke("CreateJob", dto, _db, Ctx(_recruiterId), _configuration, CancellationToken.None);

        Assert.Equal(403, Status(result));
        Assert.Empty(await _db.Jobs.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task CreateJob_ForOwnCompany_Returns201WithThatCompany()
    {
        var dto = CreateDto(_ownCompanyId);

        var result = await Invoke("CreateJob", dto, _db, Ctx(_recruiterId), _configuration, CancellationToken.None);

        Assert.Equal(201, Status(result));
        var job = await _db.Jobs.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(_ownCompanyId, job.CompanyId);
        Assert.Equal(_recruiterId, job.RecruiterId);
    }

    [Fact]
    public async Task UpdateJob_MoveToCompanyOwnedByAnotherRecruiter_Returns403AndKeepsCompany()
    {
        var job = await SeedJob(_ownCompanyId);

        var result = await Invoke("UpdateJob", job.Id, UpdateDto(_otherCompanyId), _db, Ctx(_recruiterId), CancellationToken.None);

        Assert.Equal(403, Status(result));
        var reloaded = await _db.Jobs.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(_ownCompanyId, reloaded.CompanyId);
    }

    [Fact]
    public async Task UpdateJob_MoveToOwnOtherCompany_Returns200AndMovesJob()
    {
        var job = await SeedJob(_ownCompanyId);

        var result = await Invoke("UpdateJob", job.Id, UpdateDto(_secondOwnCompanyId), _db, Ctx(_recruiterId), CancellationToken.None);

        Assert.Equal(200, Status(result));
        var reloaded = await _db.Jobs.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(_secondOwnCompanyId, reloaded.CompanyId);
    }

    [Fact]
    public async Task UpdateJob_ByNonOwnerRecruiter_Returns403AndLeavesJobUnchanged()
    {
        var job = await SeedJob(_ownCompanyId);
        var dto = new JobUpdateDto("Hijacked", "Build APIs", _ownCompanyId, "Da Nang",
            null, null, null, null, null, null, null, null);

        var result = await Invoke("UpdateJob", job.Id, dto, _db, Ctx(_otherRecruiterId), CancellationToken.None);

        Assert.Equal(403, Status(result));
        var reloaded = await _db.Jobs.IgnoreQueryFilters().SingleAsync();
        Assert.Equal("Backend Developer", reloaded.Title);
        Assert.Empty(await _db.OutboxEvents.ToListAsync());
    }

    [Fact]
    public async Task UpdateJob_UnknownId_Returns404()
    {
        var result = await Invoke("UpdateJob", Guid.NewGuid(), UpdateDto(_ownCompanyId), _db, Ctx(_recruiterId), CancellationToken.None);

        Assert.Equal(404, Status(result));
    }

    [Fact]
    public async Task DeleteJob_ByNonOwnerRecruiter_Returns403AndKeepsJobActive()
    {
        var job = await SeedJob(_ownCompanyId);

        var result = await Invoke("DeleteJob", job.Id, _db, Ctx(_otherRecruiterId), CancellationToken.None);

        Assert.Equal(403, Status(result));
        var reloaded = await _db.Jobs.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(JobStatus.Active, reloaded.Status);
        Assert.Empty(await _db.OutboxEvents.ToListAsync());
    }

    [Fact]
    public async Task DeleteJob_ByOwner_Returns204_SoftDeletesAndQueuesDeletedEvent()
    {
        var job = await SeedJob(_ownCompanyId);

        var result = await Invoke("DeleteJob", job.Id, _db, Ctx(_recruiterId), CancellationToken.None);

        Assert.Equal(204, Status(result));
        var reloaded = await _db.Jobs.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(JobStatus.Deleted, reloaded.Status);
        var outbox = await _db.OutboxEvents.SingleAsync();
        Assert.Equal(JobEventTypes.Deleted, outbox.EventType);
    }

    [Fact]
    public async Task DeleteJob_UnknownId_Returns404()
    {
        var result = await Invoke("DeleteJob", Guid.NewGuid(), _db, Ctx(_recruiterId), CancellationToken.None);

        Assert.Equal(404, Status(result));
    }

    private void AddCompany(Guid id, string name, Guid createdBy)
    {
        var company = new Company(name, createdBy);
        typeof(Company).GetProperty(nameof(Company.Id))!.SetValue(company, id);
        _db.Companies.Add(company);
    }

    private async Task<JobPosting> SeedJob(Guid companyId)
    {
        var job = new JobPosting("Backend Developer", "Build APIs", companyId, "Da Nang", _recruiterId);
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();
        return job;
    }

    private static JobCreateDto CreateDto(Guid companyId) =>
        new("Backend Developer", "Build APIs", companyId, "Da Nang",
            null, null, null, null, null, null, null, null);

    private static JobUpdateDto UpdateDto(Guid companyId) =>
        new("Backend Developer", "Build APIs", companyId, "Da Nang",
            null, null, null, null, null, null, null, null);

    private static HttpContext Ctx(Guid userId) =>
        new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Role, "Recruiter")
            ], "Test"))
        };

    private static int? Status(IResult result) =>
        Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode;

    private static async Task<IResult> Invoke(string name, params object?[] args)
    {
        var method = typeof(JobEndpoints).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;
        return await (Task<IResult>)method.Invoke(null, args)!;
    }
}
