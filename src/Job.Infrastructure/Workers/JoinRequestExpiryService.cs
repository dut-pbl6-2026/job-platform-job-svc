using Job.Core.Entities;
using Job.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace Job.Infrastructure.Workers;

/// <summary>
/// Daily job that stores <c>Expired</c> on Pending join requests whose waiting period has passed
/// (SRS AUTH-01-13). Rejected requests keep their status: the read path shows them as expired,
/// which is what allows escalation (AUTH-01-14). Idempotent, so several replicas can run it.
/// </summary>
public sealed class JoinRequestExpiryService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<JoinRequestExpiryService> _logger;

    public JoinRequestExpiryService(IServiceScopeFactory scopes, ILogger<JoinRequestExpiryService> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                var updated = await ExpireOnceAsync(stoppingToken);
                _logger.LogInformation("Join request expiry: {Count} pending requests marked Expired.", updated);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Join request expiry run failed; retrying at the next interval.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Marks every Pending request past its expiry as Expired. Returns the row count.</summary>
    public async Task<int> ExpireOnceAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JobDbContext>();
        var now = DateTimeOffset.UtcNow;
        return await db.CompanyJoinRequests
            .Where(r => r.Status == CompanyJoinRequestStatus.Pending && r.ExpiresAt <= now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, CompanyJoinRequestStatus.Expired)
                .SetProperty(r => r.UpdatedAt, now.UtcDateTime), ct);
    }
}
