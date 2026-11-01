using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Job.Api.Endpoints;
using Job.Core.Entities;
using Job.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SharedKernel.Events;
using SharedKernel.Kafka;
using JobPosting = Job.Core.Entities.Job;

namespace Job.Tests;

public sealed class AdminJobEndpointTests : IDisposable
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

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

    // ── GET /api/admin/jobs ───────────────────────────────────────────────────

    [Fact]
    public async Task GetAdminJobs_Admin_ReturnsPendingJob()
    {
        var job = NewJob(JobStatus.Pending);
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();

        var result = await AdminJobEndpointInvoker.GetAdminJobs(_db, BuildContext(_adminId, "Admin"));

        var ok = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(200, ok.StatusCode);
        var data = ResultJson(result)["data"]!.AsArray();
        Assert.Single(data);
        Assert.Equal(job.Id.ToString(), data[0]!["id"]!.GetValue<string>());
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
    public async Task GetAdminJobs_StatusFilter_ReturnsOnlyMatchingStatus()
    {
        _db.Jobs.Add(NewJob(JobStatus.Pending));
        _db.Jobs.Add(NewJob(JobStatus.Active));
        await _db.SaveChangesAsync();

        var result = await AdminJobEndpointInvoker.GetAdminJobs(
            _db, BuildContext(_adminId, "Admin"), status: "Pending");

        var data = ResultJson(result)["data"]!.AsArray();
        Assert.Single(data);
        Assert.Equal("Pending", data[0]!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task GetAdminJobs_DefaultFilter_ExcludesDeletedJobs()
    {
        _db.Jobs.Add(NewJob(JobStatus.Active));
        _db.Jobs.Add(NewJob(JobStatus.Deleted));
        await _db.SaveChangesAsync();

        var result = await AdminJobEndpointInvoker.GetAdminJobs(_db, BuildContext(_adminId, "Admin"));

        var data = ResultJson(result)["data"]!.AsArray();
        Assert.Single(data);
        Assert.Equal("Active", data[0]!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task GetAdminJobs_EmptyStatus_TreatedAsNoFilter()
    {
        _db.Jobs.Add(NewJob(JobStatus.Active));
        _db.Jobs.Add(NewJob(JobStatus.Deleted));
        await _db.SaveChangesAsync();

        var result = await AdminJobEndpointInvoker.GetAdminJobs(
            _db, BuildContext(_adminId, "Admin"), status: "");

        var data = ResultJson(result)["data"]!.AsArray();
        Assert.Single(data);
        Assert.Equal("Active", data[0]!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task GetAdminJobs_InvalidStatus_Returns400()
    {
        var result = await AdminJobEndpointInvoker.GetAdminJobs(
            _db, BuildContext(_adminId, "Admin"), status: "not-a-status");

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(400, status.StatusCode);
    }

    // ── GET /api/admin/jobs/{id} ──────────────────────────────────────────────

    [Fact]
    public async Task GetAdminJobById_PendingJob_Returns200()
    {
        var job = NewJob(JobStatus.Pending);
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();

        var result = await AdminJobEndpointInvoker.GetAdminJobById(
            job.Id, _db, BuildContext(_adminId, "Admin"));

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(200, status.StatusCode);
        Assert.Equal("Pending", ResultJson(result)["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task GetAdminJobById_UnknownJob_Returns404()
    {
        var result = await AdminJobEndpointInvoker.GetAdminJobById(
            Guid.NewGuid(), _db, BuildContext(_adminId, "Admin"));

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(404, status.StatusCode);
    }

    // ── POST /api/admin/jobs/{id}/approve ─────────────────────────────────────

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
        Assert.Equal(_adminId, job.ApprovedBy);

        var outbox = await _db.OutboxEvents.SingleAsync();
        Assert.Equal(JobEventTypes.Updated, outbox.EventType);
        Assert.Equal(job.Id.ToString(), outbox.AggregateId);
        var envelope = JsonSerializer.Deserialize<EventEnvelope<JobUpdatedEvent>>(
            outbox.Payload, KafkaJson.Options)!;
        Assert.Equal(JobEventTypes.Updated, envelope.EventType);
        Assert.Equal("Active", envelope.Payload.Status);
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

    [Fact]
    public async Task ApproveJob_UnknownJob_Returns404()
    {
        var result = await AdminJobEndpointInvoker.ApproveJob(
            Guid.NewGuid(), new ApproveJobRequest(null), _db, BuildContext(_adminId, "Admin"),
            NullLoggerFactory.Instance);

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(404, status.StatusCode);
    }

    [Fact]
    public async Task ApproveJob_NoIdentity_Returns401()
    {
        var result = await AdminJobEndpointInvoker.ApproveJob(
            Guid.NewGuid(), new ApproveJobRequest(null), _db, new DefaultHttpContext(),
            NullLoggerFactory.Instance);

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(401, status.StatusCode);
    }

    // ── POST /api/admin/jobs/{id}/reject ──────────────────────────────────────

    [Fact]
    public async Task RejectJob_PendingJob_SetsRejectedAndCreatesOutbox()
    {
        var job = NewJob(JobStatus.Pending);
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();

        var result = await AdminJobEndpointInvoker.RejectJob(
            job.Id, new RejectJobRequest("  Spam content  "), _db,
            BuildContext(_adminId, "Admin"), NullLoggerFactory.Instance);

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(200, status.StatusCode);
        Assert.Equal(JobStatus.Rejected, job.Status);
        Assert.Equal("Spam content", job.RejectionReason);
        Assert.Equal(_adminId, job.RejectedBy);
        Assert.NotNull(job.RejectedAt);

        var outbox = await _db.OutboxEvents.SingleAsync();
        var envelope = JsonSerializer.Deserialize<EventEnvelope<JobUpdatedEvent>>(
            outbox.Payload, KafkaJson.Options)!;
        Assert.Equal("Rejected", envelope.Payload.Status);
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
    public async Task RejectJob_ReasonTooLong_Returns422WithoutCreatingOutbox()
    {
        var job = NewJob(JobStatus.Pending);
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();

        var result = await AdminJobEndpointInvoker.RejectJob(
            job.Id, new RejectJobRequest(new string('x', 501)), _db,
            BuildContext(_adminId, "Admin"), NullLoggerFactory.Instance);

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(422, status.StatusCode);
        Assert.Equal(JobStatus.Pending, job.Status);
        Assert.Equal(0, await _db.OutboxEvents.CountAsync());
    }

    [Fact]
    public async Task RejectJob_NonAdmin_ReturnsForbidden()
    {
        var job = NewJob(JobStatus.Pending);
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();

        var result = await AdminJobEndpointInvoker.RejectJob(
            job.Id, new RejectJobRequest("Spam"), _db, BuildContext(_adminId, "Recruiter"),
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

    private static JsonNode ResultJson(IResult result) =>
        JsonSerializer.SerializeToNode(
            Assert.IsAssignableFrom<IValueHttpResult>(result).Value, WebJson)!;
}

internal static class AdminJobEndpointInvoker
{
    private static readonly BindingFlags Flags =
        BindingFlags.NonPublic | BindingFlags.Static;
    private static readonly MethodInfo GetAdminJobsMethod =
        typeof(AdminJobEndpoints).GetMethod("GetAdminJobs", Flags)!;
    private static readonly MethodInfo GetAdminJobByIdMethod =
        typeof(AdminJobEndpoints).GetMethod("GetAdminJobById", Flags)!;
    private static readonly MethodInfo ApproveJobMethod =
        typeof(AdminJobEndpoints).GetMethod("ApproveJob", Flags)!;
    private static readonly MethodInfo RejectJobMethod =
        typeof(AdminJobEndpoints).GetMethod("RejectJob", Flags)!;

    public static Task<IResult> GetAdminJobs(
        JobDbContext db,
        HttpContext ctx,
        string? status = null,
        Guid? companyId = null,
        Guid? categoryId = null,
        int page = 1,
        int size = 20) =>
        InvokeAsync(GetAdminJobsMethod, db, ctx, status, companyId, categoryId, page, size,
            CancellationToken.None);

    public static Task<IResult> GetAdminJobById(
        Guid id, JobDbContext db, HttpContext ctx) =>
        InvokeAsync(GetAdminJobByIdMethod, id, db, ctx, CancellationToken.None);

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
