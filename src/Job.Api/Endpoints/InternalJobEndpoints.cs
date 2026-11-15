using Job.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Job.Api.Endpoints;

/// <summary>
/// Service-to-service job lookup for app-svc (recruiter ownership checks and application intake).
/// Returns the job regardless of status, including Pending and Rejected jobs that public queries hide.
/// Not exposed through the gateway: callers must present <c>Authorization: Bearer &lt;JOB_INTERNAL_TOKEN_FOR_APP&gt;</c> (app-svc is the only caller).
/// Fails closed: an unconfigured or wrong token gets 401.
/// </summary>
public static class InternalJobEndpoints
{
    /// <summary>Configuration/env key holding the token app-svc presents on this endpoint.</summary>
    public const string AppCallerTokenKey = "JOB_INTERNAL_TOKEN_FOR_APP";

    public static WebApplication MapInternalJobEndpoints(this WebApplication app)
    {
        app.MapGet("/api/internal/jobs/{id:guid}", GetJobAsync)
            .WithTags("Internal")
            .ExcludeFromDescription();
        return app;
    }

    /// <summary>
    /// <c>Status</c> is the <c>JobStatus</c> enum name (Active, Pending, Rejected, Closed, Deleted).
    /// </summary>
    public sealed record InternalJobResponse(Guid CompanyId, string Status, string Title);

    public static async Task<IResult> GetJobAsync(
        Guid id,
        HttpContext ctx,
        JobDbContext db,
        IConfiguration config,
        CancellationToken ct = default)
    {
        var expected = config[AppCallerTokenKey];
        if (string.IsNullOrWhiteSpace(expected) ||
            !InternalStatsEndpoints.IsAuthorized(ctx.Request.Headers.Authorization.ToString(), expected))
        {
            return Results.Json(new { message = "Unauthorized" }, statusCode: 401);
        }

        // IgnoreQueryFilters: app-svc must see Pending/Rejected/Deleted jobs too, to refuse them explicitly.
        var job = await db.Jobs.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(j => j.Id == id)
            .Select(j => new { j.CompanyId, j.Status, j.Title })
            .FirstOrDefaultAsync(ct);

        return job is null
            ? Results.NotFound(new { message = "Job not found" })
            : Results.Ok(new InternalJobResponse(job.CompanyId, job.Status.ToString(), job.Title));
    }
}
