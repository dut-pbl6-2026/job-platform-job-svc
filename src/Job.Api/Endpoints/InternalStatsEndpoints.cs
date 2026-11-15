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
/// <c>Authorization: Bearer &lt;JOB_INTERNAL_TOKEN_FOR_AUTH&gt;</c> (auth-svc is the only caller).
/// </summary>
public static class InternalStatsEndpoints
{
    /// <summary>Configuration/env key holding the token auth-svc presents on this endpoint.</summary>
    public const string AuthCallerTokenKey = "JOB_INTERNAL_TOKEN_FOR_AUTH";

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
        var expected = config[AuthCallerTokenKey];
        if (string.IsNullOrWhiteSpace(expected))
        {
            // Fail closed: an unconfigured token must never turn the endpoint public.
            // Return 401 (not 503) — "misconfigured" is an auth failure, not a service outage.
            logger.LogError(
                "{TokenKey} is not configured. Returning 401 to avoid exposing the endpoint.",
                AuthCallerTokenKey);
            return Results.Json(new { message = "Unauthorized" }, statusCode: 401);
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

    internal static bool IsAuthorized(string header, string expected)
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

        // Hash both sides first so the compared arrays are always the same length
        // (FixedTimeEquals returns early on a length mismatch, which would leak the
        // expected token's length), then compare in constant time.
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(token)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
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
