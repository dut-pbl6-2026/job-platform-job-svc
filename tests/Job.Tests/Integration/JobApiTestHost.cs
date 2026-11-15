using System.Net.Http.Json;
using System.Text.Json;
using Job.Api.Endpoints;
using Job.Api.Middleware;
using Job.Api.Services;
using Job.Core.Entities;
using Job.Infrastructure.Data;
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
    public const string InternalToken = "test-internal-token";

    private readonly WebApplication _app;

    private JobApiTestHost(WebApplication app)
    {
        _app = app;
        Client = app.GetTestClient();
    }

    public HttpClient Client { get; }

    public static async Task<JobApiTestHost> StartAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [InternalStatsEndpoints.TokenConfigKey] = InternalToken,
        });

        // Captured once: AddDbContext rebuilds options per scope, so a Guid created inside the lambda
        // would give every request its own empty store.
        var dbName = "JobApi_" + Guid.NewGuid();
        builder.Services.AddDbContext<JobDbContext>(o => o.UseInMemoryDatabase(dbName));
        builder.Services.AddProblemDetails();
        builder.Services.AddHttpClient<SearchSyncPublisher>();
        builder.Services.AddSingleton<IKafkaProducer, NoOpKafkaProducer>();
        builder.Services.AddSingleton<KafkaJobEventPublisher>();

        var app = builder.Build();
        app.UseExceptionHandler();
        app.UseMiddleware<DevAuthMiddleware>();
        app.MapJobEndpoints();
        app.MapInternalJobEndpoints();
        app.MapCompanyEndpoints();
        await app.StartAsync();
        return new JobApiTestHost(app);
    }

    public async Task<Guid> CreateCompanyAsync(Guid recruiterId, string name)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/companies", recruiterId, "Recruiter",
            new { name });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
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

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, Guid? userId, string? role, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (userId is { } id)
            request.Headers.Add("X-User-Id", id.ToString());
        if (role is not null)
            request.Headers.Add("X-User-Role", role);
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
