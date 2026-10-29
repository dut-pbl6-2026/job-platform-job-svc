using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Job.Api.Endpoints;
using Job.Core.Entities;
using Job.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using JobPosting = Job.Core.Entities.Job;

namespace Job.Tests;

/// <summary>
/// feat(admin) Slice 6: token-authenticated internal stats for auth-svc aggregation.
/// No/wrong token → 401, all statuses counted except Deleted, newThisMonth scoped to UTC month.
/// </summary>
public sealed class InternalStatsEndpointTests : IDisposable
{
    private const string Token = "test-internal-token";

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly JobDbContext _db;

    public InternalStatsEndpointTests()
    {
        var options = new DbContextOptionsBuilder<JobDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new JobDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task GetStats_NoToken_Returns401()
    {
        var result = await Invoke(new DefaultHttpContext());

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(401, status.StatusCode);
    }

    [Fact]
    public async Task GetStats_WrongToken_Returns401()
    {
        var result = await Invoke(Context("Bearer wrong-token"));

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(401, status.StatusCode);
    }

    [Fact]
    public async Task GetStats_MissingBearerPrefix_Returns401()
    {
        var result = await Invoke(Context(Token));

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(401, status.StatusCode);
    }

    [Fact]
    public async Task GetStats_MissingConfiguredToken_Returns503()
    {
        var result = await Invoke(Context("Bearer anything"), configuredToken: null);

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(503, status.StatusCode);
    }

    [Fact]
    public async Task GetStats_ValidToken_ReturnsCountsByStatus()
    {
        _db.Jobs.Add(NewJob(JobStatus.Pending));
        _db.Jobs.Add(NewJob(JobStatus.Active));
        _db.Jobs.Add(NewJob(JobStatus.Active));
        _db.Jobs.Add(NewJob(JobStatus.Closed));
        _db.Jobs.Add(NewJob(JobStatus.Rejected));
        _db.Jobs.Add(NewJob(JobStatus.Deleted));
        await _db.SaveChangesAsync();

        var result = await Invoke(Context($"Bearer {Token}"));

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(200, status.StatusCode);
        var json = ResultJson(result);
        Assert.Equal(5, json["total"]!.GetValue<int>());
        Assert.Equal(1, json["pending"]!.GetValue<int>());
        Assert.Equal(2, json["active"]!.GetValue<int>());
        Assert.Equal(1, json["closed"]!.GetValue<int>());
        Assert.Equal(1, json["rejected"]!.GetValue<int>());
    }

    [Fact]
    public async Task GetStats_NewThisMonth_ExcludesPreviousMonths()
    {
        var current = NewJob(JobStatus.Active);
        var previous = NewJob(JobStatus.Active);
        var previousMonth = DateTime.UtcNow.AddMonths(-1);
        typeof(JobPosting).GetProperty(nameof(JobPosting.CreatedAt))!
            .SetValue(previous, previousMonth);
        _db.Jobs.Add(current);
        _db.Jobs.Add(previous);
        await _db.SaveChangesAsync();

        var result = await Invoke(Context($"Bearer {Token}"));

        Assert.Equal(1, ResultJson(result)["newThisMonth"]!.GetValue<int>());
    }

    [Fact]
    public async Task GetStats_DeletedJobs_AreExcludedFromTotal()
    {
        _db.Jobs.Add(NewJob(JobStatus.Active));
        _db.Jobs.Add(NewJob(JobStatus.Deleted));
        await _db.SaveChangesAsync();

        var result = await Invoke(Context($"Bearer {Token}"));

        var json = ResultJson(result);
        Assert.Equal(1, json["total"]!.GetValue<int>());
        Assert.Equal(1, json["active"]!.GetValue<int>());
    }

    private Task<IResult> Invoke(HttpContext ctx, string? configuredToken = Token) =>
        InternalStatsEndpointInvoker.GetStats(
            ctx, _db, BuildConfig(configuredToken), NullLoggerFactory.Instance);

    private static IConfiguration BuildConfig(string? token) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [InternalStatsEndpoints.TokenConfigKey] = token,
            })
            .Build();

    private static HttpContext Context(string authorization)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Authorization = authorization;
        return ctx;
    }

    private static JobPosting NewJob(JobStatus status) =>
        new("Backend Developer", "Build APIs", Guid.NewGuid(), "Da Nang", Guid.NewGuid(),
            initialStatus: status);

    private static JsonNode ResultJson(IResult result) =>
        JsonSerializer.SerializeToNode(
            Assert.IsAssignableFrom<IValueHttpResult>(result).Value, WebJson)!;
}

internal static class InternalStatsEndpointInvoker
{
    private static readonly MethodInfo GetStatsMethod =
        typeof(InternalStatsEndpoints).GetMethod(
            "GetStats", BindingFlags.NonPublic | BindingFlags.Static)!;

    public static Task<IResult> GetStats(
        HttpContext ctx,
        JobDbContext db,
        IConfiguration config,
        Microsoft.Extensions.Logging.ILoggerFactory loggerFactory) =>
        (Task<IResult>)GetStatsMethod.Invoke(
            null, [ctx, db, config, loggerFactory, CancellationToken.None])!;
}
