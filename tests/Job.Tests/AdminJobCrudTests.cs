using System.Reflection;
using System.Security.Claims;
using Job.Api.DTOs;
using Job.Api.Endpoints;
using Job.Api.Services;
using Job.Core.Entities;
using Job.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SharedKernel.Events;
using SharedKernel.Kafka;
using JobPosting = Job.Core.Entities.Job;

namespace Job.Tests;

/// <summary>Admin create / update / soft delete / hard delete (POST, PUT, DELETE /api/admin/jobs).</summary>
public sealed class AdminJobCrudTests : IDisposable
{
    private readonly JobDbContext _db;
    private readonly Guid _companyId = Guid.NewGuid();
    private readonly Guid _adminId = Guid.NewGuid();
    private readonly RecordingProducer _producer = new();
    private readonly HttpClient _http = new();
    private readonly KafkaJobEventPublisher _kafka;

    public AdminJobCrudTests()
    {
        var options = new DbContextOptionsBuilder<JobDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new JobDbContext(options);
        var company = new Company("Admin Crud Corp", Guid.NewGuid());
        typeof(Company).GetProperty(nameof(Company.Id))!.SetValue(company, _companyId);
        _db.Companies.Add(company);
        _db.SaveChanges();

        var configuration = new ConfigurationBuilder().Build();
        var searchSync = new SearchSyncPublisher(_http, configuration, NullLogger<SearchSyncPublisher>.Instance);
        _kafka = new KafkaJobEventPublisher(
            _producer, searchSync, NullLogger<KafkaJobEventPublisher>.Instance, configuration);
    }

    public void Dispose()
    {
        _kafka.Dispose();
        _http.Dispose();
        _db.Dispose();
    }

    // ── POST ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateJob_Admin_CreatesActiveJobPostedByAdminAndPublishesCreated()
    {
        var result = await Invoke("CreateJob", CreateDto(), _db, Ctx("Admin"), _kafka, NullLoggerFactory.Instance, CancellationToken.None);

        Assert.Equal(201, Status(result));
        var job = await _db.Jobs.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(JobStatus.Active, job.Status);
        Assert.Equal(_adminId, job.RecruiterId);
        Assert.Equal([JobEventTypes.Created], _producer.EventTypes);
    }

    [Fact]
    public async Task CreateJob_UnknownCompany_Returns400WithoutSaving()
    {
        var dto = CreateDto() with { CompanyId = Guid.NewGuid() };

        var result = await Invoke("CreateJob", dto, _db, Ctx("Admin"), _kafka, NullLoggerFactory.Instance, CancellationToken.None);

        Assert.Equal(400, Status(result));
        Assert.Empty(await _db.Jobs.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(_producer.EventTypes);
    }

    [Fact]
    public async Task CreateJob_MissingTitle_Returns400()
    {
        var result = await Invoke("CreateJob", CreateDto() with { Title = " " }, _db, Ctx("Admin"), _kafka, NullLoggerFactory.Instance, CancellationToken.None);

        Assert.Equal(400, Status(result));
    }

    [Fact]
    public async Task CreateJob_Recruiter_ReturnsForbidden()
    {
        var result = await Invoke("CreateJob", CreateDto(), _db, Ctx("Recruiter"), _kafka, NullLoggerFactory.Instance, CancellationToken.None);

        Assert.Equal(403, Status(result));
    }

    // ── PUT ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateJob_KeepsStatusAndPublishesUpdatedWithActualStatus()
    {
        var job = await SeedJob(JobStatus.Pending);
        var dto = UpdateDto("Senior Backend Developer");

        var result = await Invoke("UpdateJob", job.Id, dto, _db, Ctx("Admin"), _kafka, NullLoggerFactory.Instance, CancellationToken.None);

        Assert.Equal(200, Status(result));
        Assert.Equal("Senior Backend Developer", job.Title);
        Assert.Equal(JobStatus.Pending, job.Status);
        Assert.Equal([JobEventTypes.Updated], _producer.EventTypes);
        Assert.Equal("Pending", Assert.IsType<JobUpdatedEvent>(_producer.Payloads.Single()).Status);
    }

    [Fact]
    public async Task UpdateJob_DeletedJob_Returns409()
    {
        var job = await SeedJob(JobStatus.Deleted);

        var result = await Invoke("UpdateJob", job.Id, UpdateDto("x"), _db, Ctx("Admin"), _kafka, NullLoggerFactory.Instance, CancellationToken.None);

        Assert.Equal(409, Status(result));
        Assert.Empty(_producer.EventTypes);
    }

    [Fact]
    public async Task UpdateJob_UnknownJob_Returns404()
    {
        var result = await Invoke("UpdateJob", Guid.NewGuid(), UpdateDto("x"), _db, Ctx("Admin"), _kafka, NullLoggerFactory.Instance, CancellationToken.None);

        Assert.Equal(404, Status(result));
    }

    // ── DELETE (soft) ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SoftDeleteJob_MarksDeletedAndPublishesDeleted()
    {
        var job = await SeedJob(JobStatus.Active);

        var result = await Invoke("SoftDeleteJob", job.Id, _db, Ctx("Admin"), _kafka, NullLoggerFactory.Instance, CancellationToken.None);

        Assert.Equal(204, Status(result));
        Assert.Equal(JobStatus.Deleted, job.Status);
        Assert.Equal([JobEventTypes.Deleted], _producer.EventTypes);
    }

    [Fact]
    public async Task SoftDeleteJob_AlreadyDeleted_IsIdempotentAndRepublishesDeleted()
    {
        var job = await SeedJob(JobStatus.Deleted);

        var result = await Invoke("SoftDeleteJob", job.Id, _db, Ctx("Admin"), _kafka, NullLoggerFactory.Instance, CancellationToken.None);

        Assert.Equal(204, Status(result));
        Assert.Equal([JobEventTypes.Deleted], _producer.EventTypes);
    }

    [Fact]
    public async Task SoftDeleteJob_Recruiter_ReturnsForbidden()
    {
        var job = await SeedJob(JobStatus.Active);

        var result = await Invoke("SoftDeleteJob", job.Id, _db, Ctx("Recruiter"), _kafka, NullLoggerFactory.Instance, CancellationToken.None);

        Assert.Equal(403, Status(result));
        Assert.Equal(JobStatus.Active, job.Status);
    }

    // ── DELETE /permanent ─────────────────────────────────────────────────────

    [Fact]
    public async Task HardDeleteJob_SoftDeletedJob_RemovesRowAndSavedJobs()
    {
        var job = await SeedJob(JobStatus.Deleted);
        _db.SavedJobs.Add(new SavedJob(Guid.NewGuid(), job.Id));
        await _db.SaveChangesAsync();

        var result = await Invoke("HardDeleteJob", job.Id, _db, Ctx("Admin"), _kafka, NullLoggerFactory.Instance, CancellationToken.None);

        Assert.Equal(204, Status(result));
        Assert.False(await _db.Jobs.IgnoreQueryFilters().AnyAsync(j => j.Id == job.Id));
        Assert.Empty(await _db.SavedJobs.ToListAsync());
        Assert.Equal([JobEventTypes.Deleted], _producer.EventTypes);
    }

    [Theory]
    [InlineData(JobStatus.Active)]
    [InlineData(JobStatus.Pending)]
    [InlineData(JobStatus.Rejected)]
    [InlineData(JobStatus.Closed)]
    public async Task HardDeleteJob_NotSoftDeleted_Returns409AndKeepsRow(JobStatus status)
    {
        var job = await SeedJob(status);

        var result = await Invoke("HardDeleteJob", job.Id, _db, Ctx("Admin"), _kafka, NullLoggerFactory.Instance, CancellationToken.None);

        Assert.Equal(409, Status(result));
        Assert.True(await _db.Jobs.IgnoreQueryFilters().AnyAsync(j => j.Id == job.Id));
        Assert.Empty(_producer.EventTypes);
    }

    [Fact]
    public async Task HardDeleteJob_UnknownJob_Returns404()
    {
        var result = await Invoke("HardDeleteJob", Guid.NewGuid(), _db, Ctx("Admin"), _kafka, NullLoggerFactory.Instance, CancellationToken.None);

        Assert.Equal(404, Status(result));
    }

    [Fact]
    public async Task HardDeleteJob_Recruiter_ReturnsForbidden()
    {
        var job = await SeedJob(JobStatus.Deleted);

        var result = await Invoke("HardDeleteJob", job.Id, _db, Ctx("Recruiter"), _kafka, NullLoggerFactory.Instance, CancellationToken.None);

        Assert.Equal(403, Status(result));
        Assert.True(await _db.Jobs.IgnoreQueryFilters().AnyAsync(j => j.Id == job.Id));
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private JobCreateDto CreateDto() =>
        new("Backend Developer", "Build APIs", _companyId, "Da Nang",
            null, null, null, null, null, null, null, null);

    private JobUpdateDto UpdateDto(string title) =>
        new(title, "Build APIs", _companyId, "Da Nang",
            null, null, null, null, null, null, null, null);

    private async Task<JobPosting> SeedJob(JobStatus status)
    {
        var job = new JobPosting("Backend Developer", "Build APIs", _companyId, "Da Nang", Guid.NewGuid(),
            initialStatus: status);
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();
        return job;
    }

    private HttpContext Ctx(string role) =>
        new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, _adminId.ToString()),
                new Claim(ClaimTypes.Role, role)
            ], "Test"))
        };

    private static int? Status(IResult result) =>
        Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode;

    private static async Task<IResult> Invoke(string name, params object?[] args)
    {
        var method = typeof(AdminJobEndpoints).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;
        return await (Task<IResult>)method.Invoke(null, args)!;
    }

    private sealed class RecordingProducer : IKafkaProducer
    {
        public List<string> EventTypes { get; } = [];
        public List<object?> Payloads { get; } = [];

        public Task ProduceAsync<T>(
            string topic, string key, EventEnvelope<T> envelope, CancellationToken ct = default)
        {
            EventTypes.Add(envelope.EventType);
            Payloads.Add(envelope.Payload);
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }
}
