using Job.Api.Auth;
using Job.Api.DTOs;
using Job.Infrastructure.Recommendations;

namespace Job.Api.Endpoints;

/// <summary>
/// <c>GET /api/jobs/recommended</c> — personal job recommendations for job seekers (PBL6-10,
/// RECOMMEND-01). Follows api-conventions.md: 401/403 and 400 are RFC 7807 Problem Details,
/// even though older job-svc endpoints still answer 401/403 with <c>{ message }</c>.
/// </summary>
public static class RecommendationEndpoints
{
    public const int DefaultLimit = 10;
    public const int MaxLimit = RecommendationService.MaxItems;

    private const string CandidateRole = "User";

    public static WebApplication MapRecommendationEndpoints(this WebApplication app)
    {
        // Literal segment: never captured by /api/jobs/{id:guid}.
        app.MapGet("/api/jobs/recommended", GetRecommended)
            .WithTags("Jobs")
            .Produces<RecommendationResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);
        return app;
    }

    // limit/refresh are bound as strings so "abc" yields errors.limit instead of a bare framework 400.
    private static async Task<IResult> GetRecommended(
        HttpContext ctx,
        RecommendationService recommendations,
        string? limit,
        string? refresh,
        CancellationToken ct)
    {
        var (userId, role) = IdentityHelper.GetIdentity(ctx);
        if (userId is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Unauthorized",
                detail: "Thiếu hoặc sai thông tin đăng nhập.",
                type: "https://tools.ietf.org/html/rfc9110#section-15.5.2");
        }

        if (role != CandidateRole)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Forbidden",
                detail: "Chỉ ứng viên mới xem được gợi ý việc làm.",
                type: "https://tools.ietf.org/html/rfc9110#section-15.5.4");
        }

        var errors = new Dictionary<string, string[]>();
        var take = DefaultLimit;
        if (!string.IsNullOrWhiteSpace(limit)
            && (!int.TryParse(limit, out take) || take is < 1 or > MaxLimit))
        {
            errors["limit"] = [$"limit phải là số nguyên từ 1 đến {MaxLimit}."];
        }

        var force = false;
        if (!string.IsNullOrWhiteSpace(refresh) && !bool.TryParse(refresh, out force))
        {
            errors["refresh"] = ["refresh phải là true hoặc false."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var result = await recommendations.GetAsync(userId.Value, take, force, ct);
        var items = result.Items.Select(ToDto).ToList();
        return Results.Ok(new RecommendationResponse(items, items.Count, result.IsCached, result.GeneratedAt));
    }

    private static RecommendedJobDto ToDto(RecommendationItem item)
    {
        var job = item.Job;
        return new RecommendedJobDto(
            job.Id,
            job.Title,
            job.CompanyId,
            new ResourceRefDto(job.CompanyId, job.Company?.Name ?? string.Empty),
            job.CategoryId,
            job.Category is null ? null : new ResourceRefDto(job.Category.Id, job.Category.Name),
            job.Location,
            job.SalaryMin,
            job.SalaryMax,
            job.SalaryCurrency,
            job.EmploymentType,
            job.ExperienceLevel,
            job.CreatedAt,
            item.Strategy.ToString(),
            item.Reason);
    }
}
