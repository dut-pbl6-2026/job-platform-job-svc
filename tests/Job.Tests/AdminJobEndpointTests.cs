using System.Reflection;
using System.Security.Claims;
using Job.Api.Endpoints;
using Job.Core.Entities;
using Job.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using JobPosting = Job.Core.Entities.Job;

namespace Job.Tests;

public sealed class AdminJobEndpointTests : IDisposable
{
    private readonly JobDbContext _db;
    private readonly Guid _companyId = Guid.NewGuid();
    private readonly Guid _adminId = Guid.NewGuid();

    public AdminJobEndpointTests()
    {
        var options = new DbContextOptionsBuilder<JobDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new JobDbContext(options);
        _db.Database.EnsureCreated();
        var company = new Company("Admin Test Corp", Guid.NewGuid());
        typeof(Company).GetProperty(nameof(Company.Id))!.SetValue(company, _companyId);
        _db.Companies.Add(company);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task GetAdminJobs_Admin_ReturnsPendingJob()
    {
        var job = NewJob(JobStatus.Pending);
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();

        var result = await AdminJobEndpointInvoker.GetAdminJobs(_db, BuildContext(_adminId, "Admin"));

        var ok = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(200, ok.StatusCode);
    }

    [Fact]
    public async Task GetAdminJobs_NonAdmin_ReturnsForbidden()
    {
        var result = await AdminJobEndpointInvoker.GetAdminJobs(
            _db, BuildContext(Guid.NewGuid(), "Recruiter"));

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(403, status.StatusCode);
    }

    [Fact]
    public async Task ApproveJob_PendingJob_ChangesStatusAndCreatesOutbox()
    {
        var job = NewJob(JobStatus.Pending);
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();

        var result = await AdminJobEndpointInvoker.ApproveJob(
            job.Id, new ApproveJobRequest(null), _db, BuildContext(_adminId, "Admin"),
            NullLoggerFactory.Instance);

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(200, status.StatusCode);
        Assert.Equal(JobStatus.Active, job.Status);
        Assert.Equal(1, await _db.OutboxEvents.CountAsync());
    }

    [Fact]
    public async Task RejectJob_EmptyReason_Returns422WithoutCreatingOutbox()
    {
        var job = NewJob(JobStatus.Pending);
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();

        var result = await AdminJobEndpointInvoker.RejectJob(
            job.Id, new RejectJobRequest("  "), _db, BuildContext(_adminId, "Admin"),
            NullLoggerFactory.Instance);

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(422, status.StatusCode);
        Assert.Equal(JobStatus.Pending, job.Status);
        Assert.Equal(0, await _db.OutboxEvents.CountAsync());
    }

    [Fact]
    public async Task ApproveJob_AlreadyActive_Returns400()
    {
        var job = NewJob(JobStatus.Active);
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();

        var result = await AdminJobEndpointInvoker.ApproveJob(
            job.Id, new ApproveJobRequest(null), _db, BuildContext(_adminId, "Admin"),
            NullLoggerFactory.Instance);

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(400, status.StatusCode);
        Assert.Equal(0, await _db.OutboxEvents.CountAsync());
    }

    [Fact]
    public async Task ApproveJob_NonAdmin_ReturnsForbidden()
    {
        var job = NewJob(JobStatus.Pending);
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();

        var result = await AdminJobEndpointInvoker.ApproveJob(
            job.Id, new ApproveJobRequest(null), _db, BuildContext(_adminId, "Recruiter"),
            NullLoggerFactory.Instance);

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(403, status.StatusCode);
        Assert.Equal(JobStatus.Pending, job.Status);
    }

    private JobPosting NewJob(JobStatus status) =>
        new("Backend Developer", "Build APIs", _companyId, "Da Nang", Guid.NewGuid(),
            initialStatus: status);

    private static HttpContext BuildContext(Guid userId, string role) =>
        new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Role, role)
            ], "Test"))
        };
}

internal static class AdminJobEndpointInvoker
{
    private static readonly BindingFlags Flags =
        BindingFlags.NonPublic | BindingFlags.Static;
    private static readonly MethodInfo GetAdminJobsMethod =
        typeof(AdminJobEndpoints).GetMethod("GetAdminJobs", Flags)!;
    private static readonly MethodInfo ApproveJobMethod =
        typeof(AdminJobEndpoints).GetMethod("ApproveJob", Flags)!;
    private static readonly MethodInfo RejectJobMethod =
        typeof(AdminJobEndpoints).GetMethod("RejectJob", Flags)!;

    public static Task<IResult> GetAdminJobs(
        JobDbContext db, HttpContext ctx) =>
        InvokeAsync(GetAdminJobsMethod, db, ctx, null, null, null, 1, 20, CancellationToken.None);

    public static Task<IResult> ApproveJob(
        Guid id, ApproveJobRequest body, JobDbContext db, HttpContext ctx,
        ILoggerFactory loggerFactory) =>
        InvokeAsync(ApproveJobMethod, id, body, db, ctx, loggerFactory, CancellationToken.None);

    public static Task<IResult> RejectJob(
        Guid id, RejectJobRequest body, JobDbContext db, HttpContext ctx,
        ILoggerFactory loggerFactory) =>
        InvokeAsync(RejectJobMethod, id, body, db, ctx, loggerFactory, CancellationToken.None);

    private static async Task<IResult> InvokeAsync(MethodInfo method, params object?[] args) =>
        await (Task<IResult>)method.Invoke(null, args)!;
}
