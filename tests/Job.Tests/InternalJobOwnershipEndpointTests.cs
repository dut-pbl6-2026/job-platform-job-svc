using Job.Api.Endpoints;
using Job.Core.Entities;
using Job.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using JobPosting = Job.Core.Entities.Job;

namespace Job.Tests;

/// <summary>
/// App-svc recruiter ownership lookup. Must answer for Pending jobs (public queries hide them),
/// and must fail closed without the internal service token.
/// </summary>
public sealed class InternalJobOwnershipEndpointTests : IDisposable
{
    private const string Token = "test-internal-token";
    private readonly JobDbContext _db;
    private readonly IConfiguration _config;

    public InternalJobOwnershipEndpointTests()
    {
        var options = new DbContextOptionsBuilder<JobDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new JobDbContext(options);
        _db.Database.EnsureCreated();
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [InternalStatsEndpoints.TokenConfigKey] = Token })
            .Build();
    }

    public void Dispose() => _db.Dispose();

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
        var none = await InternalJobOwnershipEndpoints.GetJobCompanyAsync(jobId, Context(null), _db, _config);
        var wrong = await InternalJobOwnershipEndpoints.GetJobCompanyAsync(jobId, Context("Bearer nope"), _db, _config);
        Assert.Equal(401, ((IStatusCodeHttpResult)none).StatusCode);
        Assert.Equal(401, ((IStatusCodeHttpResult)wrong).StatusCode);
    }

    [Fact]
    public async Task UnconfiguredToken_FailsClosed()
    {
        var noToken = new ConfigurationBuilder().Build();
        var result = await InternalJobOwnershipEndpoints.GetJobCompanyAsync(
            Guid.NewGuid(), Context("Bearer anything"), _db, noToken);
        Assert.Equal(401, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task PendingJob_ReturnsItsCompany()
    {
        var companyId = Guid.NewGuid();
        var job = CreatePendingJob(companyId);
        _db.Jobs.Add(job);
        await _db.SaveChangesAsync();

        var result = await InternalJobOwnershipEndpoints.GetJobCompanyAsync(
            job.Id, Context($"Bearer {Token}"), _db, _config);

        var ok = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(200, ok.StatusCode);
        var body = System.Text.Json.JsonSerializer.Serialize(((IValueHttpResult)result).Value);
        Assert.Contains(companyId.ToString(), body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnknownJob_Returns404()
    {
        var result = await InternalJobOwnershipEndpoints.GetJobCompanyAsync(
            Guid.NewGuid(), Context($"Bearer {Token}"), _db, _config);
        Assert.Equal(404, ((IStatusCodeHttpResult)result).StatusCode);
    }

    private static JobPosting CreatePendingJob(Guid companyId) =>
        new("Pending job", "Awaiting admin approval", companyId, "Da Nang", Guid.NewGuid(), JobStatus.Pending);
}
