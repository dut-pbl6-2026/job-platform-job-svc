using System.Net.Http.Json;
using System.Text.Json;
using Job.Api.Endpoints;
using Job.Api.Middleware;
using Job.Api.Services;
using Job.Core.Entities;
using Job.Infrastructure.Data;
using Job.Infrastructure.Clients;
using Job.Infrastructure.Workers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SharedKernel.Kafka;
using JobPosting = Job.Core.Entities.Job;

namespace Job.Tests.Integration;

/// <summary>
/// In-process job-svc over TestServer with EF InMemory, using the same wiring as
/// RecommendationTestHost. The real Program.cs is not used: it runs Npgsql migrations and
/// fails fast without PostgreSQL, so it cannot start under WebApplicationFactory in tests.
/// Endpoints are mapped the same way Program.cs maps them.
/// </summary>
internal sealed class JobApiTestHost : IAsyncDisposable
{
    /// <summary>Token app-svc presents on <c>/api/internal/jobs/{id}</c> (JOB_INTERNAL_TOKEN_FOR_APP).</summary>
    public const string InternalToken = "test-internal-token";
    /// <summary>Token auth-svc presents on <c>/api/internal/stats</c> (JOB_INTERNAL_TOKEN_FOR_AUTH).</summary>
    public const string AuthInternalToken = "test-auth-internal-token";

    private readonly WebApplication _app;

    private JobApiTestHost(WebApplication app)
    {
        _app = app;
        Client = app.GetTestClient();
    }

    public HttpClient Client { get; }

    public IServiceProvider Services => _app.Services;

    public static async Task<JobApiTestHost> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [InternalJobEndpoints.AppCallerTokenKey] = InternalToken,
            [InternalStatsEndpoints.AuthCallerTokenKey] = AuthInternalToken,
        });

        // Captured once: AddDbContext rebuilds options per scope, so a Guid created inside the lambda
        // would give every request its own empty store.
        var dbName = "JobApi_" + Guid.NewGuid();
        builder.Services.AddDbContext<JobDbContext>(o => o.UseInMemoryDatabase(dbName));
        builder.Services.AddProblemDetails();
        builder.Services.AddSingleton<IKafkaProducer, NoOpKafkaProducer>();
        // Unconfigured link client: approve/support-approve answer 503 unless a test registers a fake.
        builder.Services.AddTransient<IAuthCompanyLinker>(sp =>
            new AuthCompanyLinker(new HttpClient(), sp.GetRequiredService<IConfiguration>()));

        var app = builder.Build();
        app.UseExceptionHandler();
        app.UseMiddleware<DevAuthMiddleware>();
        app.MapJobEndpoints();
        app.MapInternalJobEndpoints();
        app.MapCompanyEndpoints();
        app.MapInviteCodeEndpoints();
        app.MapInternalCompanyEndpoints();
        app.MapCompanyJoinRequestEndpoints();
        await app.StartAsync();
        return new JobApiTestHost(app);
    }

    /// <summary>
    /// Seeds a Company created by <paramref name="recruiterId"/>. POST /api/companies is always 409 for a
    /// Recruiter (AUTH-01-06), so tests seed the row directly.
    /// </summary>
    public async Task<Guid> CreateCompanyAsync(Guid recruiterId, string name)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JobDbContext>();
        var company = new Company(name, recruiterId);
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        return company.Id;
    }

    /// <summary>Seeds a job in the given status directly in the database (Pending/Rejected have no public write path here).</summary>
    public async Task<Guid> SeedJobAsync(Guid companyId, Guid recruiterId, JobStatus status)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JobDbContext>();
        var job = new JobPosting("Seeded " + status, "Desc", companyId, "Da Nang", recruiterId, status);
        db.Jobs.Add(job);
        await db.SaveChangesAsync();
        return job.Id;
    }

    public async Task<JobPosting?> LoadJobAsync(Guid jobId)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JobDbContext>();
        return await db.Jobs.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(j => j.Id == jobId);
    }

    public async Task<List<OutboxEvent>> LoadOutboxAsync()
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JobDbContext>();
        return await db.OutboxEvents.AsNoTracking().OrderBy(e => e.CreatedAt).ToListAsync();
    }

    /// <summary>Runs one outbox publish cycle with the given producer and clock.</summary>
    public async Task<bool> RunOutboxCycleAsync(IKafkaProducer producer, TimeProvider clock)
    {
        var publisher = new OutboxPublisherService(
            _app.Services.GetRequiredService<IServiceScopeFactory>(),
            producer,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OutboxPublisherService>.Instance,
            clock);
        return await publisher.PublishPendingAsync(CancellationToken.None);
    }

    public Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, Guid? userId, string? role, object? body = null, Guid? companyId = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (userId is { } id)
            request.Headers.Add("X-User-Id", id.ToString());
        if (role is not null)
            request.Headers.Add("X-User-Role", role);
        // Mirrors the gateway, which copies the JWT company_id claim into X-User-Company.
        if (companyId is { } company)
            request.Headers.Add("X-User-Company", company.ToString());
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return Client.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
