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

namespace Job.Tests;

/// <summary>
/// Jobs publish instantly (owner decision): the endpoint always creates an Active job. The old
/// FEATURE_ADMIN_APPROVAL flag is gone, so even a leftover flag value must not change the status.
/// </summary>
public sealed class JobCreateInstantPublishTests : IDisposable
{
    private readonly JobDbContext _db;
    private readonly Guid _companyId = Guid.NewGuid();
    private readonly Guid _recruiterId = Guid.NewGuid();

    public JobCreateInstantPublishTests()
    {
        var options = new DbContextOptionsBuilder<JobDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new JobDbContext(options);
        _db.Database.EnsureCreated();

        var company = new Company("Instant Publish Corp", _recruiterId);
        typeof(Company).GetProperty(nameof(Company.Id))!.SetValue(company, _companyId);
        _db.Companies.Add(company);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task CreateJob_IsActiveImmediately_WithNoApprovalFlag()
    {
        var dto = new JobCreateDto(
            "Backend Developer", "Build APIs", _companyId, "Da Nang",
            null, null, null, null, null, null, null, null);

        var result = await InvokeCreateJob(dto);

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(201, status.StatusCode);
        var job = await _db.Jobs.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(JobStatus.Active, job.Status);

        // The job.created event carries Active, so search-svc indexes it immediately.
        var outbox = await _db.OutboxEvents.SingleAsync();
        Assert.Equal(JobEventTypes.Created, outbox.EventType);
        Assert.Contains("\"status\":\"Active\"", outbox.Payload);
    }

    [Fact]
    public async Task CreateJob_StaleAdminApprovalFlagInConfiguration_IsIgnored()
    {
        // Older deployments may still define the removed flag; it must not gate publishing.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FEATURE_ADMIN_APPROVAL"] = "true",
            })
            .Build();
        Assert.Equal("true", configuration["FEATURE_ADMIN_APPROVAL"]); // the key is readable, but unused

        var dto = new JobCreateDto(
            "Backend Developer", "Build APIs", _companyId, "Da Nang",
            null, null, null, null, null, null, null, null);

        var result = await InvokeCreateJob(dto);

        Assert.Equal(201, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        var job = await _db.Jobs.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(JobStatus.Active, job.Status);
    }

    [Fact]
    public async Task CreateJob_NewJobIsVisibleToThePublicQuery()
    {
        var dto = new JobCreateDto(
            "Backend Developer", "Build APIs", _companyId, "Da Nang",
            null, null, null, null, null, null, null, null);

        await InvokeCreateJob(dto);

        // The default query filter is what public reads use: an instantly published job is visible.
        Assert.Equal(1, await _db.Jobs.CountAsync());
    }

    private HttpContext BuildContext() =>
        new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, _recruiterId.ToString()),
                new Claim(ClaimTypes.Role, "Recruiter")
            ], "Test"))
        };

    private async Task<IResult> InvokeCreateJob(JobCreateDto dto)
    {
        var method = typeof(JobEndpoints).GetMethod(
            "CreateJob", BindingFlags.NonPublic | BindingFlags.Static)!;
        return await (Task<IResult>)method.Invoke(
            null,
            [dto, _db, BuildContext(), CancellationToken.None])!;
    }
}
