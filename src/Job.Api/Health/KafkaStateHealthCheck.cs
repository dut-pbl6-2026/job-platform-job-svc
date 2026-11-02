using Job.Api.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using SharedKernel.Kafka;

namespace Job.Api.Health;

/// <summary>
/// Reports the circuit-breaker state already maintained by KafkaJobEventPublisher/KafkaHealthCheckService
/// (no extra broker call). Kafka down is Degraded, not Unhealthy: HTTP sync fallback keeps the service usable.
/// </summary>
public sealed class KafkaStateHealthCheck(
    IOptions<KafkaOptions> options, IServiceProvider services) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.Value.BootstrapServers))
            return Task.FromResult(HealthCheckResult.Healthy("Kafka not configured (HTTP sync only)."));

        // Resolved lazily: the publisher is only needed (and only meaningful) when Kafka is configured.
        var publisher = services.GetRequiredService<KafkaJobEventPublisher>();
        return Task.FromResult(publisher.IsKafkaHealthy
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Degraded("Kafka circuit open; using HTTP fallback."));
    }
}
