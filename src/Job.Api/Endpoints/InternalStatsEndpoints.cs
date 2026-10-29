using System.Security.Cryptography;
using System.Text;
using Job.Core.Entities;
using Job.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using JobPosting = Job.Core.Entities.Job;

namespace Job.Api.Endpoints;

/// <summary>
/// Service-to-service stats endpoint for the admin dashboard aggregation
/// (feat(admin) Slice 6). Not exposed through the gateway: callers must present
/// <c>Authorization: Bearer &lt;INTERNAL_SERVICE_TOKEN&gt;</c>.
/// </summary>
public static class InternalStatsEndpoints
{
    /// <summary>Configuration/env key holding the shared internal service token.</summary>
    public const string TokenConfigKey = "INTERNAL_SERVICE_TOKEN";

    private const string BearerPrefix = "Bearer ";

    public static WebApplication MapInternalStatsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/internal/stats", GetStats)
            .WithTags("Internal")
            .ExcludeFromDescription();
        return app;
    }

    private static async Task<IResult> GetStats(
        HttpContext ctx,
        JobDbContext db,
        IConfiguration config,
        ILoggerFactory loggerFactory,
        CancellationToken ct = default)
    {
        var logger = loggerFactory.CreateLogger("InternalStatsEndpoints");
        var expected = config[TokenConfigKey];
        if (string.IsNullOrWhiteSpace(expected))
        {
            // Fail closed: an unconfigured token must never turn the endpoint public.
            logger.LogError(
                "{TokenKey} is not configured. /api/internal/stats is unavailable.",
                TokenConfigKey);
            return Results.Json(new { message = "Internal stats unavailable." }, statusCode: 503);
        }

        if (!IsAuthorized(ctx.Request.Headers.Authorization.ToString(), expected))
        {
            return Results.Json(new { message = "Unauthorized" }, statusCode: 401);
        }

        // IgnoreQueryFilters: Pending/Rejected are intentionally hidden from public queries
        // but must be counted. Deleted jobs are excluded — they no longer exist for admins.
        var jobs = db.Jobs.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(j => j.Status != JobStatus.Deleted);

        var counts = await jobs
            .GroupBy(j => j.Status)
            .Select(g => new { Status = g.Key, Total = g.Count() })
            .ToDictionaryAsync(x => x.Status, x => x.Total, ct);

        var now = DateTime.UtcNow;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var newThisMonth = await jobs.CountAsync(j => j.CreatedAt >= monthStart, ct);

        return Results.Ok(new InternalJobStats(
            Total: counts.Values.Sum(),
            Pending: counts.GetValueOrDefault(JobStatus.Pending),
            Active: counts.GetValueOrDefault(JobStatus.Active),
            Closed: counts.GetValueOrDefault(JobStatus.Closed),
            Rejected: counts.GetValueOrDefault(JobStatus.Rejected),
            NewThisMonth: newThisMonth));
    }

    private static bool IsAuthorized(string header, string expected)
    {
        if (!header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var token = header[BearerPrefix.Length..].Trim();
        if (token.Length == 0)
        {
            return false;
        }

        // Constant-time comparison — do not leak token length/prefix via timing.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(token),
            Encoding.UTF8.GetBytes(expected));
    }
}

/// <summary>Aggregated job counts for the admin dashboard.</summary>
public sealed record InternalJobStats(
    int Total,
    int Pending,
    int Active,
    int Closed,
    int Rejected,
    int NewThisMonth);
