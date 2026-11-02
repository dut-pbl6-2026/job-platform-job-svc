using Job.Infrastructure.Data;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Job.Api.Health;

/// <summary>Postgres reachability via the DbContext, bounded by a short timeout.</summary>
public sealed class DatabaseHealthCheck(JobDbContext db) : IHealthCheck
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(Timeout);
        try
        {
            return await db.Database.CanConnectAsync(cts.Token)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Cannot connect to database.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("Database check timed out.");
        }
        catch (Exception ex)
        {
            // Exception type only — messages may contain connection-string fragments.
            return HealthCheckResult.Unhealthy($"Database check failed: {ex.GetType().Name}");
        }
    }
}
