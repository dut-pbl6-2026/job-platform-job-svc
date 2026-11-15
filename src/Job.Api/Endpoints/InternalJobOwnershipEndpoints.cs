using Job.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Job.Api.Endpoints;

/// <summary>
/// Service-to-service ownership lookup for app-svc recruiter checks. Returns the owning company
/// of any job, including Pending and Rejected jobs that public queries hide. Not exposed through
/// the gateway: callers must present <c>Authorization: Bearer &lt;INTERNAL_SERVICE_TOKEN&gt;</c>.
/// Fails closed: an unconfigured or wrong token gets 401.
/// </summary>
public static class InternalJobOwnershipEndpoints
{
    public static WebApplication MapInternalJobOwnershipEndpoints(this WebApplication app)
    {
        app.MapGet("/api/internal/jobs/{id:guid}/company", GetJobCompanyAsync)
            .WithTags("Internal")
            .ExcludeFromDescription();
        return app;
    }

    public static async Task<IResult> GetJobCompanyAsync(
        Guid id,
        HttpContext ctx,
        JobDbContext db,
        IConfiguration config,
        CancellationToken ct = default)
    {
        var expected = config[InternalStatsEndpoints.TokenConfigKey];
        if (string.IsNullOrWhiteSpace(expected) ||
            !InternalStatsEndpoints.IsAuthorized(ctx.Request.Headers.Authorization.ToString(), expected))
        {
            return Results.Json(new { message = "Unauthorized" }, statusCode: 401);
        }

        // IgnoreQueryFilters: ownership must be known for Pending/Rejected jobs too.
        var companyId = await db.Jobs.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(j => j.Id == id)
            .Select(j => (Guid?)j.CompanyId)
            .FirstOrDefaultAsync(ct);

        return companyId is null
            ? Results.NotFound(new { message = "Job not found" })
            : Results.Ok(new { companyId = companyId.Value });
    }
}
