using System.Text.Json;
using Job.Api.Health;
using Job.Api.Observability;
using Job.Infrastructure.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using SharedKernel.Kafka;

namespace Job.Tests;

public sealed class HealthEndpointTests
{
    private static async Task<WebApplication> StartAsync(Action<IServiceCollection> configure)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        configure(builder.Services);
        builder.Services.AddJobHealthChecks();
        var app = builder.Build();
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.MapJobHealth();
        await app.StartAsync();
        return app;
    }

    private static void UseUnreachableDb(IServiceCollection s)
    {
        s.AddDbContext<JobDbContext>(o => o.UseNpgsql(
            // Port 1 refuses immediately -> deterministic failure, no flaky network wait.
            "Host=127.0.0.1;Port=1;Timeout=1;Command Timeout=1"));
        s.AddSingleton<IOptions<KafkaOptions>>(Options.Create(new KafkaOptions { BootstrapServers = "" }));
    }

    [Fact]
    public async Task Health_ReturnsStaticContract_WithoutTouchingDependencies()
    {
        await using var app = await StartAsync(s => UseUnreachableDb(s));
        var res = await app.GetTestClient().GetAsync("/health");

        Assert.Equal(System.Net.HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("""{"status":"ok","service":"job"}""", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Ready_Returns503_WhenDatabaseUnreachable()
    {
        await using var app = await StartAsync(s => UseUnreachableDb(s));
        var res = await app.GetTestClient().GetAsync("/health/ready");

        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal("unhealthy", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("unhealthy", doc.RootElement.GetProperty("checks").GetProperty("postgres").GetProperty("status").GetString());
        Assert.Equal("healthy", doc.RootElement.GetProperty("checks").GetProperty("kafka").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Ready_Returns200_WhenAllChecksPass()
    {
        await using var app = await StartAsync(s =>
        {
            s.AddSingleton<IOptions<KafkaOptions>>(Options.Create(new KafkaOptions { BootstrapServers = "" }));
            s.AddDbContext<JobDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        });
        var res = await app.GetTestClient().GetAsync("/health/ready");

        Assert.Equal(System.Net.HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal("healthy", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task KafkaCheck_NotConfigured_IsHealthy()
    {
        var check = new KafkaStateHealthCheck(
            Options.Create(new KafkaOptions { BootstrapServers = "" }), new ServiceCollection().BuildServiceProvider());
        var result = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task CorrelationId_IsGenerated_WhenAbsent()
    {
        await using var app = await StartAsync(s => UseUnreachableDb(s));
        var res = await app.GetTestClient().GetAsync("/health");

        Assert.True(res.Headers.TryGetValues("X-Correlation-Id", out var values));
        Assert.True(Guid.TryParse(values!.Single(), out _));
    }

    [Fact]
    public async Task CorrelationId_IsEchoed_WhenProvided()
    {
        await using var app = await StartAsync(s => UseUnreachableDb(s));
        var req = new HttpRequestMessage(HttpMethod.Get, "/health");
        req.Headers.Add("X-Correlation-Id", "abc-123");
        var res = await app.GetTestClient().SendAsync(req);

        Assert.Equal("abc-123", res.Headers.GetValues("X-Correlation-Id").Single());
    }

    [Fact]
    public async Task CorrelationId_IsReplaced_WhenInvalid()
    {
        await using var app = await StartAsync(s => UseUnreachableDb(s));
        var req = new HttpRequestMessage(HttpMethod.Get, "/health");
        req.Headers.Add("X-Correlation-Id", "bad value with spaces");
        var res = await app.GetTestClient().SendAsync(req);

        Assert.NotEqual("bad value with spaces", res.Headers.GetValues("X-Correlation-Id").Single());
    }
}
