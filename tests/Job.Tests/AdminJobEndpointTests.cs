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

/// <summary>
/// Unit tests for the admin job endpoints (list/detail plus moderation). The approval endpoints are gone:
/// jobs publish instantly, so the admin surface only lists, takes down and restores.
/// </summary>
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
    public async Task GetAdminJobs_LegacyPendingRow_IsStillListedForLegacyRendering()
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
        Assert.Equal("Pending", data[0]!["status"]!.GetValue<string>());
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
    public async Task GetAdminJobs_DeletedFilter_ShowsTakenDownJobs()
    {
        var job = NewJob(JobStatus.Active);
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();
        job.Takedown("Spam", _adminId);
        await _db.SaveChangesAsync();

        var result = await AdminJobEndpointInvoker.GetAdminJobs(
            _db, BuildContext(_adminId, "Admin"), status: "Deleted");

        var data = ResultJson(result)["data"]!.AsArray();
        Assert.Single(data);
        Assert.Equal("Deleted", data[0]!["status"]!.GetValue<string>());
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

    [Fact]
    public async Task GetAdminJobs_TitleSearch_IsCaseInsensitive()
    {
        _db.Jobs.Add(NewJob(JobStatus.Active));
        _db.Jobs.Add(new JobPosting("Kế toán tổng hợp", "Sổ sách", _companyId, "Hà Nội", Guid.NewGuid(),
            initialStatus: JobStatus.Active));
        await _db.SaveChangesAsync();

        var result = await AdminJobEndpointInvoker.GetAdminJobs(
            _db, BuildContext(_adminId, "Admin"), q: "  backend ");

        var data = ResultJson(result)["data"]!.AsArray();
        Assert.Single(data);
        Assert.Equal("Backend Developer", data[0]!["title"]!.GetValue<string>());
        Assert.Equal("Da Nang", data[0]!["location"]!.GetValue<string>());
    }

    // ── GET /api/admin/jobs/{id} ──────────────────────────────────────────────

    [Fact]
    public async Task GetAdminJobById_TakenDownJob_ShowsModerationAudit()
    {
        var job = NewJob(JobStatus.Active);
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();
        job.Takedown("Misleading salary", _adminId);
        await _db.SaveChangesAsync();

        var result = await AdminJobEndpointInvoker.GetAdminJobById(
            job.Id, _db, BuildContext(_adminId, "Admin"));

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(200, status.StatusCode);
        var body = ResultJson(result);
        Assert.Equal("Deleted", body["status"]!.GetValue<string>());
        Assert.Equal("Misleading salary", body["moderationReason"]!.GetValue<string>());
        Assert.Equal(_adminId, body["moderatedBy"]!.GetValue<Guid>());
        Assert.NotNull(body["moderatedAt"]);
    }

    [Fact]
    public async Task GetAdminJobById_UnknownJob_Returns404()
    {
        var result = await AdminJobEndpointInvoker.GetAdminJobById(
            Guid.NewGuid(), _db, BuildContext(_adminId, "Admin"));

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(404, status.StatusCode);
    }

    // ── POST /api/admin/jobs/{id}/takedown ────────────────────────────────────

    [Fact]
    public async Task Takedown_ActiveJob_PersistsReasonActorTimeAndQueuesDelete()
    {
        var job = NewJob(JobStatus.Active);
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();

        var result = await AdminJobEndpointInvoker.TakedownJob(
            job.Id, new TakedownJobRequest("  Misleading salary  "), _db,
            BuildContext(_adminId, "Admin"), NullLoggerFactory.Instance);

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(200, status.StatusCode);
        Assert.Equal(JobStatus.Deleted, job.Status);
        Assert.Equal("Misleading salary", job.ModerationReason);
        Assert.Equal(_adminId, job.ModeratedBy);
        Assert.NotNull(job.ModeratedAt);

        var outbox = await _db.OutboxEvents.SingleAsync();
        Assert.Equal(JobEventTypes.Deleted, outbox.EventType);
        Assert.Equal(job.Id.ToString(), outbox.AggregateId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Takedown_MissingReason_Returns422AndChangesNothing(string? reason)
    {
        var job = NewJob(JobStatus.Active);
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();

        var result = await AdminJobEndpointInvoker.TakedownJob(
            job.Id, new TakedownJobRequest(reason), _db,
            BuildContext(_adminId, "Admin"), NullLoggerFactory.Instance);

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(422, status.StatusCode);
        Assert.Equal(JobStatus.Active, job.Status);
        Assert.Null(job.ModerationReason);
        Assert.Equal(0, await _db.OutboxEvents.CountAsync());
    }

    [Fact]
    public async Task Takedown_ReasonTooLong_Returns422AndChangesNothing()
    {
        var job = NewJob(JobStatus.Active);
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();

        var result = await AdminJobEndpointInvoker.TakedownJob(
            job.Id, new TakedownJobRequest(new string('x', JobPosting.ModerationReasonMaxLength + 1)), _db,
            BuildContext(_adminId, "Admin"), NullLoggerFactory.Instance);

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(422, status.StatusCode);
        Assert.Equal(JobStatus.Active, job.Status);
        Assert.Equal(0, await _db.OutboxEvents.CountAsync());
    }

    [Fact]
    public async Task Takedown_NonAdmin_ReturnsForbidden()
    {
        var job = NewJob(JobStatus.Active);
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();

        var result = await AdminJobEndpointInvoker.TakedownJob(
            job.Id, new TakedownJobRequest("Spam"), _db,
            BuildContext(_adminId, "Recruiter"), NullLoggerFactory.Instance);

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(403, status.StatusCode);
        Assert.Equal(JobStatus.Active, job.Status);
        Assert.Equal(0, await _db.OutboxEvents.CountAsync());
    }

    [Fact]
    public async Task Takedown_UnknownJob_Returns404()
    {
        var result = await AdminJobEndpointInvoker.TakedownJob(
            Guid.NewGuid(), new TakedownJobRequest("Spam"), _db,
            BuildContext(_adminId, "Admin"), NullLoggerFactory.Instance);

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(404, status.StatusCode);
    }

    [Fact]
    public async Task Takedown_IsIdempotent_AndKeepsTheOriginalAudit()
    {
        var job = NewJob(JobStatus.Active);
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();

        await AdminJobEndpointInvoker.TakedownJob(
            job.Id, new TakedownJobRequest("First reason"), _db,
            BuildContext(_adminId, "Admin"), NullLoggerFactory.Instance);
        var firstTakenDownAt = job.ModeratedAt;

        var second = await AdminJobEndpointInvoker.TakedownJob(
            job.Id, new TakedownJobRequest("Second reason"), _db,
            BuildContext(_adminId, "Admin"), NullLoggerFactory.Instance);

        Assert.Equal(200, Assert.IsAssignableFrom<IStatusCodeHttpResult>(second).StatusCode);
        Assert.Equal("First reason", job.ModerationReason);
        Assert.Equal(firstTakenDownAt, job.ModeratedAt);
        Assert.Equal(1, await _db.OutboxEvents.CountAsync());
    }

    [Fact]
    public async Task Takedown_RecruiterDeletedJob_RecordsModeration()
    {
        // A recruiter DELETE leaves no moderation record; admin takedown must still record the audit.
        var job = NewJob(JobStatus.Active);
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();
        job.SoftDelete();
        await _db.SaveChangesAsync();

        var result = await AdminJobEndpointInvoker.TakedownJob(
            job.Id, new TakedownJobRequest("Fraud"), _db,
            BuildContext(_adminId, "Admin"), NullLoggerFactory.Instance);

        Assert.Equal(200, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Equal("Fraud", job.ModerationReason);
        Assert.Equal(_adminId, job.ModeratedBy);
        Assert.Single(await _db.OutboxEvents.ToListAsync());
    }

    // ── POST /api/admin/jobs/{id}/restore ─────────────────────────────────────

    [Fact]
    public async Task Restore_TakenDownJob_ReactivatesClearsAuditAndReindexes()
    {
        var job = NewJob(JobStatus.Active);
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();
        job.Takedown("Spam", _adminId);
        await _db.SaveChangesAsync();
        _db.OutboxEvents.RemoveRange(_db.OutboxEvents);
        await _db.SaveChangesAsync();

        var result = await AdminJobEndpointInvoker.RestoreJob(
            job.Id, _db, BuildContext(_adminId, "Admin"), NullLoggerFactory.Instance);

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(200, status.StatusCode);
        Assert.Equal(JobStatus.Active, job.Status);
        Assert.Null(job.ModerationReason);
        Assert.Null(job.ModeratedBy);
        Assert.Null(job.ModeratedAt);

        var outbox = await _db.OutboxEvents.SingleAsync();
        Assert.Equal(JobEventTypes.Updated, outbox.EventType);
        var envelope = JsonSerializer.Deserialize<EventEnvelope<JobUpdatedEvent>>(
            outbox.Payload, KafkaJson.Options)!;
        Assert.Equal("Active", envelope.Payload.Status);
        // Restoring re-indexes the job; it must keep its original creation time, not jump to "newest".
        Assert.Equal(job.CreatedAt, envelope.Payload.CreatedAt);
    }

    [Fact]
    public async Task Restore_ActiveJob_Returns409WithoutAnEvent()
    {
        var job = NewJob(JobStatus.Active);
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();

        var result = await AdminJobEndpointInvoker.RestoreJob(
            job.Id, _db, BuildContext(_adminId, "Admin"), NullLoggerFactory.Instance);

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(409, status.StatusCode);
        Assert.Equal(0, await _db.OutboxEvents.CountAsync());
    }

    [Fact]
    public async Task Restore_JobDeletedByItsRecruiter_Returns409()
    {
        var job = NewJob(JobStatus.Active);
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();
        job.SoftDelete();
        await _db.SaveChangesAsync();

        var result = await AdminJobEndpointInvoker.RestoreJob(
            job.Id, _db, BuildContext(_adminId, "Admin"), NullLoggerFactory.Instance);

        Assert.Equal(409, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Equal(JobStatus.Deleted, job.Status);
    }

    [Fact]
    public async Task Restore_NonAdmin_ReturnsForbidden()
    {
        var job = NewJob(JobStatus.Active);
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();
        job.Takedown("Spam", _adminId);
        await _db.SaveChangesAsync();

        var result = await AdminJobEndpointInvoker.RestoreJob(
            job.Id, _db, BuildContext(_adminId, "Recruiter"), NullLoggerFactory.Instance);

        Assert.Equal(403, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Equal(JobStatus.Deleted, job.Status);
    }

    [Fact]
    public async Task Restore_UnknownJob_Returns404()
    {
        var result = await AdminJobEndpointInvoker.RestoreJob(
            Guid.NewGuid(), _db, BuildContext(_adminId, "Admin"), NullLoggerFactory.Instance);

        Assert.Equal(404, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }

    [Fact]
    public async Task Takedown_NoIdentity_Returns401()
    {
        var result = await AdminJobEndpointInvoker.TakedownJob(
            Guid.NewGuid(), new TakedownJobRequest("Spam"), _db, new DefaultHttpContext(),
            NullLoggerFactory.Instance);

        Assert.Equal(401, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
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
    private static readonly MethodInfo TakedownJobMethod =
        typeof(AdminJobEndpoints).GetMethod("TakedownJob", Flags)!;
    private static readonly MethodInfo RestoreJobMethod =
        typeof(AdminJobEndpoints).GetMethod("RestoreJob", Flags)!;

    public static Task<IResult> GetAdminJobs(
        JobDbContext db,
        HttpContext ctx,
        string? status = null,
        Guid? companyId = null,
        Guid? categoryId = null,
        int page = 1,
        int size = 20,
        string? q = null) =>
        InvokeAsync(GetAdminJobsMethod, db, ctx, status, q, companyId, categoryId, page, size,
            CancellationToken.None);

    public static Task<IResult> GetAdminJobById(
        Guid id, JobDbContext db, HttpContext ctx) =>
        InvokeAsync(GetAdminJobByIdMethod, id, db, ctx, CancellationToken.None);

    public static Task<IResult> TakedownJob(
        Guid id, TakedownJobRequest body, JobDbContext db, HttpContext ctx,
        ILoggerFactory loggerFactory) =>
        InvokeAsync(TakedownJobMethod, id, body, db, ctx, loggerFactory, CancellationToken.None);

    public static Task<IResult> RestoreJob(
        Guid id, JobDbContext db, HttpContext ctx, ILoggerFactory loggerFactory) =>
        InvokeAsync(RestoreJobMethod, id, db, ctx, loggerFactory, CancellationToken.None);

    private static async Task<IResult> InvokeAsync(MethodInfo method, params object?[] args) =>
        await (Task<IResult>)method.Invoke(null, args)!;
}
