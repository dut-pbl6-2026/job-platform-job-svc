using System.Text.Json;
using Job.Api.Observability;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Job.Api.Health;

public static class HealthEndpoints
{
    public static IServiceCollection AddJobHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("postgres", tags: ["ready"], timeout: TimeSpan.FromSeconds(4))
            .AddCheck<KafkaStateHealthCheck>("kafka", tags: ["ready"], timeout: TimeSpan.FromSeconds(2));
        return services;
    }

    public static WebApplication MapJobHealth(this WebApplication app)
    {
        // REL-06: cheap liveness — no dependencies; contract used by Render healthCheckPath.
        app.MapGet("/health", () => Results.Ok(new { status = "ok", service = ObservabilityExtensions.ServiceName }))
           .WithTags("Health")
           .ExcludeFromDescription();

        // PBL6-49: readiness — real dependency checks. 503 only when a check is Unhealthy.
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("ready"),
            ResponseWriter = WriteReadiness
        }).ExcludeFromDescription();
        return app;
    }

    private static Task WriteReadiness(HttpContext ctx, HealthReport report)
    {
        ctx.Response.ContentType = "application/json";
        var body = new
        {
            status = report.Status.ToString().ToLowerInvariant(),
            service = ObservabilityExtensions.ServiceName,
            checks = report.Entries.ToDictionary(e => e.Key, e => new
            {
                status = e.Value.Status.ToString().ToLowerInvariant(),
                description = e.Value.Description
            })
        };
        return ctx.Response.WriteAsync(JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }
}
