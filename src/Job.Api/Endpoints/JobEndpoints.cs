using System.Security.Claims;
using Job.Api.DTOs;
using Job.Core.Entities;
using Job.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using JobPosting = Job.Core.Entities.Job;

namespace Job.Api.Endpoints;

/// <summary>
/// Job CRUD endpoints per SRS JOB-01-01 to JOB-01-05.
/// POST   /api/jobs          — Recruiter only
/// GET    /api/jobs/recruiter — Recruiter's own jobs (paginated, filterable)
/// GET    /api/jobs/{id}     — public, non-deleted only
/// PUT    /api/jobs/{id}     — Recruiter (owner only)
/// DELETE /api/jobs/{id}     — Recruiter (owner only), soft delete
/// </summary>
public static class JobEndpoints
{
    public static WebApplication MapJobEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/jobs").WithTags("Jobs");
        group.MapPost("/", CreateJob);
        group.MapGet("/recruiter", GetRecruiterJobs);
        group.MapGet("/{id:guid}", GetJobById);
        group.MapPut("/{id:guid}", UpdateJob);
        group.MapDelete("/{id:guid}", DeleteJob);
        return app;
    }

    // Helpers to avoid Results.Forbid()/Unauthorized() which require an auth handler.
    // In Development no JWT handler is registered (DevAuthMiddleware only), so Forbid()
    // would throw InvalidOperationException. Use explicit JSON status codes instead.
    private static IResult UnauthorizedResult() =>
        Results.Json(new { message = "Unauthorized. Missing or invalid X-User-Id." }, statusCode: 401);

    private static IResult ForbiddenResult(string message = "Forbidden. Recruiter role required.") =>
        Results.Json(new { message }, statusCode: 403);

    private static async Task<IResult> CreateJob(
        JobCreateDto dto,
        JobDbContext db,
        HttpContext ctx)
    {
        var (recruiterId, role) = GetIdentity(ctx);
        if (recruiterId is null)
            return UnauthorizedResult();
        if (role != "Recruiter")
            return ForbiddenResult();

        if (string.IsNullOrWhiteSpace(dto.Title))
            return Results.ValidationProblem(new Dictionary<string, string[]>
            { ["title"] = ["Title is required."] });

        if (string.IsNullOrWhiteSpace(dto.Description))
            return Results.ValidationProblem(new Dictionary<string, string[]>
            { ["description"] = ["Description is required."] });

        if (string.IsNullOrWhiteSpace(dto.Location))
            return Results.ValidationProblem(new Dictionary<string, string[]>
            { ["location"] = ["Location is required."] });

        var companyExists = await db.Companies.AnyAsync(c => c.Id == dto.CompanyId);
        if (!companyExists)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            { ["companyId"] = ["Company not found."] });

        if (dto.CategoryId.HasValue)
        {
            var categoryExists = await db.Categories.AnyAsync(c => c.Id == dto.CategoryId.Value);
            if (!categoryExists)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                { ["categoryId"] = ["Category not found."] });
        }

        JobPosting job;
        try
        {
            job = new JobPosting(
                dto.Title, dto.Description, dto.CompanyId, dto.Location,
                Guid.Parse(recruiterId), dto.SalaryMin, dto.SalaryMax,
                dto.SalaryCurrency ?? "VND", dto.CategoryId, dto.Requirements,
                dto.Benefits, dto.EmploymentType ?? "FullTime", dto.ExperienceLevel ?? "Entry");
        }
        catch (ArgumentException ex)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            { ["request"] = [ex.Message] });
        }

        db.Jobs.Add(job);
        await db.SaveChangesAsync();
        return Results.Created($"/api/jobs/{job.Id}", new { id = job.Id, message = "Job created" });
    }

    private static async Task<IResult> GetRecruiterJobs(
        JobDbContext db, HttpContext ctx, string? status = null, int page = 1, int size = 10)
    {
        var (recruiterId, role) = GetIdentity(ctx);
        if (recruiterId is null)
            return UnauthorizedResult();
        if (role != "Recruiter")
            return ForbiddenResult();

        size = Math.Clamp(size, 1, 100);
        page = Math.Max(1, page);
        var recruitGuid = Guid.Parse(recruiterId);

        var query = db.Jobs.IgnoreQueryFilters().Where(j => j.RecruiterId == recruitGuid);

        if (!string.IsNullOrEmpty(status) &&
            Enum.TryParse<JobStatus>(status, ignoreCase: true, out var statusEnum))
            query = query.Where(j => j.Status == statusEnum);

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(j => j.CreatedAt)
            .Skip((page - 1) * size).Take(size)
            .Include(j => j.Company).Include(j => j.Category).ToListAsync();

        var dtos = items.Select(JobDetailDto.From).ToList();
        var totalPages = (int)Math.Ceiling((double)total / size);
        return Results.Ok(new PaginatedResponse<JobDetailDto>(dtos, total, page, size, totalPages));
    }

    private static async Task<IResult> GetJobById(Guid id, JobDbContext db)
    {
        var job = await db.Jobs
            .Include(j => j.Company).Include(j => j.Category)
            .FirstOrDefaultAsync(j => j.Id == id);

        if (job is null) return Results.NotFound(new { message = "Job not found" });

        // Atomic increment to avoid lost-update race under concurrent GETs.
        // InMemory provider (unit tests) does not support ExecuteUpdateAsync, so fallback to tracked increment.
        if (db.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")
        {
            job.IncrementView();
            await db.SaveChangesAsync();
            return Results.Ok(JobDetailDto.From(job));
        }
        else
        {
            await db.Jobs.Where(j => j.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.ViewCount, j => j.ViewCount + 1));
            // Return DTO with incremented count without mutating private-setter entity
            var dto = JobDetailDto.From(job) with { ViewCount = job.ViewCount + 1 };
            return Results.Ok(dto);
        }
    }

    private static async Task<IResult> UpdateJob(
        Guid id, JobUpdateDto dto, JobDbContext db, HttpContext ctx)
    {
        var (recruiterId, role) = GetIdentity(ctx);
        if (recruiterId is null)
            return UnauthorizedResult();
        if (role != "Recruiter")
            return ForbiddenResult();

        var job = await db.Jobs.IgnoreQueryFilters().FirstOrDefaultAsync(j => j.Id == id);
        if (job is null) return Results.NotFound(new { message = "Job not found" });
        if (job.RecruiterId != Guid.Parse(recruiterId))
            return ForbiddenResult("Forbidden. You do not own this job.");

        if (string.IsNullOrWhiteSpace(dto.Title))
            return Results.ValidationProblem(new Dictionary<string, string[]>
            { ["title"] = ["Title is required."] });

        var companyExists = await db.Companies.AnyAsync(c => c.Id == dto.CompanyId);
        if (!companyExists)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            { ["companyId"] = ["Company not found."] });

        if (dto.CategoryId.HasValue)
        {
            var categoryExists = await db.Categories.AnyAsync(c => c.Id == dto.CategoryId.Value);
            if (!categoryExists)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                { ["categoryId"] = ["Category not found."] });
        }

        try
        {
            job.Update(
                dto.Title, dto.Description, dto.CompanyId, dto.Location,
                dto.SalaryMin, dto.SalaryMax, dto.SalaryCurrency ?? "VND", dto.CategoryId,
                dto.Requirements, dto.Benefits, dto.EmploymentType ?? "FullTime", dto.ExperienceLevel ?? "Entry");
        }
        catch (ArgumentException ex)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            { ["request"] = [ex.Message] });
        }

        await db.SaveChangesAsync();
        return Results.Ok(new { message = "Job updated" });
    }

    private static async Task<IResult> DeleteJob(Guid id, JobDbContext db, HttpContext ctx)
    {
        var (recruiterId, role) = GetIdentity(ctx);
        if (recruiterId is null)
            return UnauthorizedResult();
        if (role != "Recruiter")
            return ForbiddenResult();

        var job = await db.Jobs.IgnoreQueryFilters().FirstOrDefaultAsync(j => j.Id == id);
        if (job is null) return Results.NotFound(new { message = "Job not found" });
        if (job.RecruiterId != Guid.Parse(recruiterId))
            return ForbiddenResult("Forbidden. You do not own this job.");

        job.SoftDelete();
        await db.SaveChangesAsync();
        return Results.NoContent();
    }

    private static (string? userId, string? role) GetIdentity(HttpContext ctx)
    {
        var userId = ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var role = ctx.User.FindFirst(ClaimTypes.Role)?.Value;
        return (userId, role);
    }
}