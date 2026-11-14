using System.Net.Http.Json;
using Job.Api.Endpoints;
using Job.Api.Middleware;
using Job.Api.Services;
using Job.Infrastructure.Data;
using Job.Infrastructure.Observability;
using Job.Infrastructure.Recommendations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using SharedKernel.Kafka;

namespace Job.Tests.Recommendations;

/// <summary>PBL6-10 slice 7: recommendation metrics are exported on /metrics (Prometheus).</summary>
public sealed class RecommendationMetricsTests
{
    [Fact]
    public async Task Metrics_ExposeServedByStrategyDurationAndCache()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseTestServer();
        var dbName = "RecommendationMetrics_" + Guid.NewGuid();
        builder.Services.AddDbContext<JobDbContext>(o => o.UseInMemoryDatabase(dbName));
        builder.Services.AddProblemDetails();
        builder.Services.AddHttpClient<SearchSyncPublisher>();
        builder.Services.AddSingleton<IKafkaProducer, NoOpKafkaProducer>();
        builder.Services.AddSingleton<KafkaJobEventPublisher>();
        builder.Services.AddRecommendations();
        builder.Services.AddOpenTelemetry().WithMetrics(m => m
            .AddMeter(RecommendationMetrics.MeterName)
            .AddPrometheusExporter());

        await using var app = builder.Build();
        app.UseMiddleware<DevAuthMiddleware>();
        app.MapJobEndpoints();
        app.MapRecommendationEndpoints();
        app.MapCompanyEndpoints();
        app.MapPrometheusScrapingEndpoint();
        await app.StartAsync();
        var client = app.GetTestClient();

        var recruiter = Guid.NewGuid();
        using (var company = RecommendationTestHost.Request(HttpMethod.Post, "/api/companies", recruiter, "Recruiter"))
        {
            company.Content = System.Net.Http.Json.JsonContent.Create(new { name = "Metrics " + Guid.NewGuid().ToString("N")[..6] });
            var companyId = (await (await client.SendAsync(company)).Content
                .ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetGuid();
            using var job = RecommendationTestHost.Request(HttpMethod.Post, "/api/jobs", recruiter, "Recruiter");
            job.Content = System.Net.Http.Json.JsonContent.Create(new
            {
                title = "Lập trình viên .NET",
                description = "Mô tả",
                companyId,
                location = "Đà Nẵng",
            });
            Assert.Equal(System.Net.HttpStatusCode.Created, (await client.SendAsync(job)).StatusCode);
        }

        var user = Guid.NewGuid();
        await client.SendAsync(RecommendationTestHost.Request(HttpMethod.Get, "/api/jobs/recommended", user, "User"));
        await client.SendAsync(RecommendationTestHost.Request(HttpMethod.Get, "/api/jobs/recommended", user, "User"));

        var metrics = await client.GetStringAsync("/metrics");

        Assert.Contains("job_recommendations_served_total{", metrics);
        Assert.Contains("strategy=\"Popular\"", metrics);
        Assert.Contains("job_recommendations_duration_milliseconds_bucket", metrics);
        Assert.Contains("job_recommendations_cache_total{", metrics);
        Assert.Contains("result=\"hit\"", metrics);
        Assert.Contains("result=\"miss\"", metrics);
    }

    [Fact]
    public void BackfillRows_IsAnInstrumentOfTheMeter()
    {
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        var seen = new List<string>();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == RecommendationMetrics.MeterName)
                seen.Add(instrument.Name);
        };
        listener.Start();

        RecommendationMetrics.BackfillRows(0);

        Assert.Contains("job.recommendations.backfill.rows", seen);
    }
}
