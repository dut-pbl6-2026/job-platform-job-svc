using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Job.Api.Endpoints;
using Job.Api.Middleware;
using Job.Api.Services;
using Job.Core.Entities;
using Job.Infrastructure.Data;
using Job.Infrastructure.Recommendations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SharedKernel.Kafka;

namespace Job.Tests.Recommendations;

/// <summary>
/// In-process job-svc for recommendation tests (TestServer + EF InMemory + DevAuthMiddleware).
/// Test data enters through the real paths (D-7): jobs via POST /api/jobs, deletions via
/// DELETE /api/jobs/{id}, applications via the Kafka consumer handler. No seeding of
/// JobInteractions, no mocks of the service under test.
/// </summary>
internal sealed class RecommendationTestHost : IAsyncDisposable
{
    public static readonly Guid ItCategory = new("7d09cd31-5580-41a2-948e-00bfbfdc8e3b");
    public static readonly Guid FinanceCategory = new("9e9769ef-0457-41fa-86be-7ea16a7259db");

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly WebApplication _app;

    private RecommendationTestHost(WebApplication app, Guid recruiterId, Guid companyId)
    {
        _app = app;
        RecruiterId = recruiterId;
        CompanyId = companyId;
        Client = app.GetTestClient();
        Consumer = new TestApplicationConsumer(app.Services);
    }

    public HttpClient Client { get; }

    public TestApplicationConsumer Consumer { get; }

    public Guid RecruiterId { get; }

    public Guid CompanyId { get; }

    public IServiceProvider Services => _app.Services;

    public static async Task<RecommendationTestHost> StartAsync(IDictionary<string, string?>? settings = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(settings ?? new Dictionary<string, string?>());

        var dbName = "Recommendations_" + Guid.NewGuid();
        builder.Services.AddDbContext<JobDbContext>(o => o.UseInMemoryDatabase(dbName));
        builder.Services.AddProblemDetails();
        builder.Services.AddHttpClient<SearchSyncPublisher>();
        builder.Services.AddSingleton<IKafkaProducer, NoOpKafkaProducer>();
        builder.Services.AddSingleton<KafkaJobEventPublisher>();
        builder.Services.AddRecommendations();

        var app = builder.Build();
        app.UseExceptionHandler();
        app.UseMiddleware<DevAuthMiddleware>();
        app.MapJobEndpoints();
        app.MapCompanyEndpoints();
        app.MapCategoryEndpoints();
        await app.StartAsync();

        using (var scope = app.Services.CreateScope())
            await SeedData.SeedCategoriesAsync(scope.ServiceProvider.GetRequiredService<JobDbContext>());

        var recruiterId = Guid.NewGuid();
        var client = app.GetTestClient();
        using var request = Request(HttpMethod.Post, "/api/companies", recruiterId, "Recruiter");
        request.Content = JsonContent.Create(new { name = "Công ty ABC " + Guid.NewGuid().ToString("N")[..6] });
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var companyId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        return new RecommendationTestHost(app, recruiterId, companyId);
    }

    /// <summary>Creates a job through POST /api/jobs as the host's recruiter.</summary>
    public async Task<Guid> CreateJobAsync(string title, Guid? categoryId, string location)
    {
        using var request = Request(HttpMethod.Post, "/api/jobs", RecruiterId, "Recruiter");
        request.Content = JsonContent.Create(new
        {
            title,
            description = "Mô tả công việc",
            companyId = CompanyId,
            location,
            salaryMin = 15_000_000,
            salaryMax = 25_000_000,
            categoryId,
            experienceLevel = "Mid",
        });
        var response = await Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    /// <summary>Creates a job while admin approval is on, so it stays Pending.</summary>
    public async Task<Guid> CreatePendingJobAsync(string title, Guid? categoryId, string location)
    {
        _app.Configuration["FEATURE_ADMIN_APPROVAL"] = "true";
        try
        {
            return await CreateJobAsync(title, categoryId, location);
        }
        finally
        {
            _app.Configuration["FEATURE_ADMIN_APPROVAL"] = "false";
        }
    }

    /// <summary>Soft-deletes through DELETE /api/jobs/{id}.</summary>
    public async Task DeleteJobAsync(Guid jobId)
    {
        using var request = Request(HttpMethod.Delete, $"/api/jobs/{jobId}", RecruiterId, "Recruiter");
        var response = await Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    /// <summary>
    /// Closes a job. job-svc has no HTTP endpoint that closes a job, so this goes through the
    /// domain method (<see cref="Job.Core.Entities.Job.Close"/>), the same code an endpoint would call.
    /// </summary>
    public async Task CloseJobAsync(Guid jobId)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JobDbContext>();
        var job = await db.Jobs.SingleAsync(j => j.Id == jobId);
        job.Close();
        await db.SaveChangesAsync();
    }

    /// <summary>Opens a job detail page (GET /api/jobs/{id}), which increments ViewCount.</summary>
    public async Task ViewJobAsync(Guid jobId, int times = 1)
    {
        for (var i = 0; i < times; i++)
            Assert.Equal(HttpStatusCode.OK, (await Client.GetAsync($"/api/jobs/{jobId}")).StatusCode);
    }

    /// <summary>Calls the service directly (slice 4); endpoint tests use <see cref="Client"/>.</summary>
    public async Task<RecommendationResult> RecommendAsync(Guid userId, int limit = 10)
    {
        using var scope = _app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<RecommendationService>()
            .GetAsync(userId, limit, CancellationToken.None);
    }

    public static HttpRequestMessage Request(HttpMethod method, string url, Guid? userId, string? role)
    {
        var request = new HttpRequestMessage(method, url);
        if (userId is { } id)
            request.Headers.Add("X-User-Id", id.ToString());
        if (role is not null)
            request.Headers.Add("X-User-Role", role);
        return request;
    }

    public static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(), WebJson);

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
