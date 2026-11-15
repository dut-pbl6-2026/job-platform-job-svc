using Job.Api.Endpoints;
using Job.Core.Entities;
using Job.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using JobPosting = Job.Core.Entities.Job;

namespace Job.Tests;

/// <summary>
/// App-svc job lookup. Must answer for jobs of any status (public queries hide Pending/Rejected),
/// and must fail closed without the internal service token.
/// </summary>
public sealed class InternalJobEndpointTests : IDisposable
{
    private const string Token = "test-internal-token";
    private readonly JobDbContext _db;
    private readonly IConfiguration _config;

    public InternalJobEndpointTests()
    {
        var options = new DbContextOptionsBuilder<JobDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new JobDbContext(options);
        _db.Database.EnsureCreated();
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [InternalJobEndpoints.AppCallerTokenKey] = Token })
            .Build();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task StatsCallerToken_IsRejected_EvenWhenConfigured()
    {
        // Only app-svc's token opens this endpoint; the token issued to auth-svc must not.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [InternalJobEndpoints.AppCallerTokenKey] = Token,
                [InternalStatsEndpoints.AuthCallerTokenKey] = "test-auth-caller-token",
            })
            .Build();

        var result = await InternalJobEndpoints.GetJobAsync(
            Guid.NewGuid(), Context("Bearer test-auth-caller-token"), _db, config);

        Assert.Equal(401, ((IStatusCodeHttpResult)result).StatusCode);
    }

    private static HttpContext Context(string? authorization)
    {
        var ctx = new DefaultHttpContext();
        if (authorization is not null)
            ctx.Request.Headers.Authorization = authorization;
        return ctx;
    }

    [Fact]
    public async Task NoOrWrongToken_Returns401()
    {
        var jobId = Guid.NewGuid();
        var none = await InternalJobEndpoints.GetJobAsync(jobId, Context(null), _db, _config);
        var wrong = await InternalJobEndpoints.GetJobAsync(jobId, Context("Bearer nope"), _db, _config);
        Assert.Equal(401, ((IStatusCodeHttpResult)none).StatusCode);
        Assert.Equal(401, ((IStatusCodeHttpResult)wrong).StatusCode);
    }

    [Fact]
    public async Task UnconfiguredToken_FailsClosed()
    {
        var noToken = new ConfigurationBuilder().Build();
        var result = await InternalJobEndpoints.GetJobAsync(
            Guid.NewGuid(), Context("Bearer anything"), _db, noToken);
        Assert.Equal(401, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task PendingJob_ReturnsCompanyStatusAndTitle()
    {
        var companyId = Guid.NewGuid();
        var job = new JobPosting("Pending job", "Awaiting admin approval", companyId, "Da Nang", Guid.NewGuid(), JobStatus.Pending);
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();

        var result = await InternalJobEndpoints.GetJobAsync(job.Id, Context($"Bearer {Token}"), _db, _config);

        Assert.Equal(200, ((IStatusCodeHttpResult)result).StatusCode);
        var body = Assert.IsType<InternalJobEndpoints.InternalJobResponse>(((IValueHttpResult)result).Value);
        Assert.Equal(companyId, body.CompanyId);
        Assert.Equal("Pending", body.Status);
        Assert.Equal("Pending job", body.Title);
    }

    [Fact]
    public async Task ActiveJob_ReturnsActiveStatus()
    {
        var job = new JobPosting("Active job", "Open", Guid.NewGuid(), "Hanoi", Guid.NewGuid());
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();

        var result = await InternalJobEndpoints.GetJobAsync(job.Id, Context($"Bearer {Token}"), _db, _config);

        var body = Assert.IsType<InternalJobEndpoints.InternalJobResponse>(((IValueHttpResult)result).Value);
        Assert.Equal("Active", body.Status);
    }

    [Fact]
    public async Task UnknownJob_Returns404()
    {
        var result = await InternalJobEndpoints.GetJobAsync(
            Guid.NewGuid(), Context($"Bearer {Token}"), _db, _config);
        Assert.Equal(404, ((IStatusCodeHttpResult)result).StatusCode);
    }
}
