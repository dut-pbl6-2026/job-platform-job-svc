using Job.Api.Auth;
using Job.Api.DTOs;
using Job.Core.Entities;
using Job.Infrastructure.Data;
using Job.Infrastructure.Outbox;
using Microsoft.EntityFrameworkCore;
using JobPosting = Job.Core.Entities.Job;

namespace Job.Api.Endpoints;

/// <summary>
/// Admin-only job management endpoints (feat(admin): Slice 4, moderation only).
/// GET    /api/admin/jobs                  — paginated job list (all statuses, optional title search q)
/// GET    /api/admin/jobs/{id}             — full job detail
/// POST   /api/admin/jobs                  — create a job (Active, posted by the admin)
/// PUT    /api/admin/jobs/{id}             — update any job that is not soft-deleted
/// DELETE /api/admin/jobs/{id}             — soft delete (idempotent)
/// DELETE /api/admin/jobs/{id}/permanent   — hard delete a soft-deleted job from the DB
/// POST   /api/admin/jobs/{id}/takedown    — take an Active/Closed job down with a required reason
/// POST   /api/admin/jobs/{id}/restore     — restore a job taken down by moderation
/// All endpoints require X-User-Role: Admin.
/// There is no approve/reject step: company jobs publish instantly. Moderation can only remove and restore.
/// Uses IgnoreQueryFilters() so Admin can see soft-deleted jobs.
/// Every write that changes a job (create, update, takedown, restore, soft and hard delete) adds its
/// job event to the outbox in the same SaveChangesAsync call, so search-svc follows the change.
/// </summary>
public static class AdminJobEndpoints
{
    private static IResult ForbiddenResult(string message = "Forbidden. Admin role required.") =>
        Results.Json(new { message }, statusCode: 403);

    private static IResult UnauthorizedResult() =>
        Results.Json(new { message = "Unauthorized. Missing or invalid X-User-Id." }, statusCode: 401);

    public static WebApplication MapAdminJobEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/admin/jobs").WithTags("Admin - Jobs");
        group.MapGet("/", GetAdminJobs);
        group.MapGet("/{id:guid}", GetAdminJobById);
        group.MapPost("/", CreateJob);
        group.MapPut("/{id:guid}", UpdateJob);
        group.MapDelete("/{id:guid}", SoftDeleteJob);
        group.MapDelete("/{id:guid}/permanent", HardDeleteJob);
        group.MapPost("/{id:guid}/takedown", TakedownJob);
        group.MapPost("/{id:guid}/restore", RestoreJob);
        return app;
    }

    // ── GET /api/admin/jobs ───────────────────────────────────────────────────

    private static async Task<IResult> GetAdminJobs(
        JobDbContext db,
        HttpContext ctx,
        string? status = null,
        string? q = null,
        Guid? companyId = null,
        Guid? categoryId = null,
        int page = 1,
        int size = 20,
        CancellationToken ct = default)
    {
        var (adminId, role) = IdentityHelper.GetIdentity(ctx);
        if (adminId is null) return UnauthorizedResult();
        if (role != "Admin") return ForbiddenResult();

        page = Math.Max(1, page);
        size = Math.Clamp(size, 1, 100);

        // IgnoreQueryFilters: Admin can see every status, including soft-deleted (taken down) jobs
        var query = db.Jobs.IgnoreQueryFilters()
            .AsNoTracking()
            .Include(j => j.Company)
            .Include(j => j.Category)
            .AsQueryable();

        // Filter out Deleted unless explicitly requested
        if (string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(j => j.Status != JobStatus.Deleted);
        }
        else if (Enum.TryParse<JobStatus>(status, ignoreCase: true, out var statusEnum)
                 && Enum.IsDefined(statusEnum))
        {
            query = query.Where(j => j.Status == statusEnum);
        }
        else
        {
            return Results.Json(
                new { message = $"Invalid status '{status}'. Valid values: {string.Join(", ", Enum.GetNames<JobStatus>())}." },
                statusCode: 400);
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToLower();
            query = query.Where(j => j.Title.ToLower().Contains(term));
        }

        if (companyId.HasValue)
            query = query.Where(j => j.CompanyId == companyId.Value);

        if (categoryId.HasValue)
            query = query.Where(j => j.CategoryId == categoryId.Value);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(j => j.CreatedAt)
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync(ct);

        var dtos = items.Select(JobAdminListDto.From).ToList();
        var totalPages = (int)Math.Ceiling((double)total / size);

        return Results.Ok(new
        {
            data = dtos,
            pagination = new { page, pageSize = size, totalItems = total, totalPages }
        });
    }

    // ── GET /api/admin/jobs/{id} ──────────────────────────────────────────────

    private static async Task<IResult> GetAdminJobById(
        Guid id,
        JobDbContext db,
        HttpContext ctx,
        CancellationToken ct = default)
    {
        var (adminId, role) = IdentityHelper.GetIdentity(ctx);
        if (adminId is null) return UnauthorizedResult();
        if (role != "Admin") return ForbiddenResult();

        var job = await db.Jobs.IgnoreQueryFilters()
            .AsNoTracking()
            .Include(j => j.Company)
            .Include(j => j.Category)
            .FirstOrDefaultAsync(j => j.Id == id, ct);

        if (job is null)
            return Results.NotFound(new { message = "Job not found." });

        return Results.Ok(JobAdminDetailDto.From(job));
    }

    // ── POST /api/admin/jobs ──────────────────────────────────────────────────

    private static async Task<IResult> CreateJob(
        JobCreateDto dto,
        JobDbContext db,
        HttpContext ctx,
        ILoggerFactory loggerFactory,
        CancellationToken ct = default)
    {
        var (adminId, role) = IdentityHelper.GetIdentity(ctx);
        if (adminId is null) return UnauthorizedResult();
        if (role != "Admin") return ForbiddenResult();

        var (company, category, invalid) = await ValidateJobInputAsync(
            db, dto.Title, dto.Description, dto.Location, dto.CompanyId, dto.CategoryId, ct);
        if (invalid is not null) return invalid;

        JobPosting job;
        try
        {
            // An admin-created job skips the approval queue; the admin is recorded as its poster.
            job = new JobPosting(
                dto.Title, dto.Description, dto.CompanyId, dto.Location,
                adminId.Value,
                initialStatus: JobStatus.Active,
                salaryMin: dto.SalaryMin,
                salaryMax: dto.SalaryMax,
                salaryCurrency: dto.SalaryCurrency ?? "VND",
                categoryId: dto.CategoryId,
                requirements: dto.Requirements,
                benefits: dto.Benefits,
                employmentType: dto.EmploymentType ?? "FullTime",
                experienceLevel: dto.ExperienceLevel ?? "Entry");
        }
        catch (ArgumentException ex)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = [ex.Message] });
        }

        db.Jobs.Add(job);
        db.OutboxEvents.Add(JobOutboxEvents.Created(job, company!.Name, category?.Name));
        await db.SaveChangesAsync(ct);

        loggerFactory.CreateLogger("AdminJobEndpoints").LogInformation(
            "Admin {AdminId} created job {JobId}.", adminId.Value, job.Id);
        return Results.Created($"/api/admin/jobs/{job.Id}", new { id = job.Id, message = "Job created." });
    }

    // ── PUT /api/admin/jobs/{id} ──────────────────────────────────────────────

    private static async Task<IResult> UpdateJob(
        Guid id,
        JobUpdateDto dto,
        JobDbContext db,
        HttpContext ctx,
        ILoggerFactory loggerFactory,
        CancellationToken ct = default)
    {
        var (adminId, role) = IdentityHelper.GetIdentity(ctx);
        if (adminId is null) return UnauthorizedResult();
        if (role != "Admin") return ForbiddenResult();

        var job = await db.Jobs.IgnoreQueryFilters().FirstOrDefaultAsync(j => j.Id == id, ct);
        if (job is null)
            return Results.NotFound(new { message = "Job not found." });
        if (job.Status == JobStatus.Deleted)
            return Results.Json(new { message = "A deleted job cannot be edited." }, statusCode: 409);

        var (company, category, invalid) = await ValidateJobInputAsync(
            db, dto.Title, dto.Description, dto.Location, dto.CompanyId, dto.CategoryId, ct);
        if (invalid is not null) return invalid;

        try
        {
            job.Update(
                dto.Title, dto.Description, dto.CompanyId, dto.Location,
                dto.SalaryMin, dto.SalaryMax, dto.SalaryCurrency ?? "VND", dto.CategoryId,
                dto.Requirements, dto.Benefits, dto.EmploymentType ?? "FullTime", dto.ExperienceLevel ?? "Entry");
        }
        catch (ArgumentException ex)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = [ex.Message] });
        }

        // Update() never changes the status: the event carries the actual one so a Closed job
        // edited here stays out of the active-only company listing.
        db.OutboxEvents.Add(JobOutboxEvents.Updated(job, company!.Name, category?.Name));
        await db.SaveChangesAsync(ct);

        loggerFactory.CreateLogger("AdminJobEndpoints").LogInformation(
            "Admin {AdminId} updated job {JobId}.", adminId.Value, job.Id);
        return Results.Ok(new { message = "Job updated." });
    }

    // ── DELETE /api/admin/jobs/{id} ───────────────────────────────────────────

    private static async Task<IResult> SoftDeleteJob(
        Guid id,
        JobDbContext db,
        HttpContext ctx,
        ILoggerFactory loggerFactory,
        CancellationToken ct = default)
    {
        var (adminId, role) = IdentityHelper.GetIdentity(ctx);
        if (adminId is null) return UnauthorizedResult();
        if (role != "Admin") return ForbiddenResult();

        var job = await db.Jobs.IgnoreQueryFilters().FirstOrDefaultAsync(j => j.Id == id, ct);
        if (job is null)
            return Results.NotFound(new { message = "Job not found." });

        // Queued even when the job was already deleted: idempotent on search-svc,
        // and it removes a document left in the index by an earlier event.
        if (job.Status != JobStatus.Deleted)
            job.SoftDelete();
        db.OutboxEvents.Add(JobOutboxEvents.Deleted(job.Id));
        await db.SaveChangesAsync(ct);

        loggerFactory.CreateLogger("AdminJobEndpoints").LogInformation(
            "Admin {AdminId} soft-deleted job {JobId}.", adminId.Value, job.Id);
        return Results.NoContent();
    }

    // ── DELETE /api/admin/jobs/{id}/permanent ─────────────────────────────────

    private static async Task<IResult> HardDeleteJob(
        Guid id,
        JobDbContext db,
        HttpContext ctx,
        ILoggerFactory loggerFactory,
        CancellationToken ct = default)
    {
        var (adminId, role) = IdentityHelper.GetIdentity(ctx);
        if (adminId is null) return UnauthorizedResult();
        if (role != "Admin") return ForbiddenResult();

        var job = await db.Jobs.IgnoreQueryFilters().FirstOrDefaultAsync(j => j.Id == id, ct);
        if (job is null)
            return Results.NotFound(new { message = "Job not found." });
        if (job.Status != JobStatus.Deleted)
            return Results.Json(
                new { message = "Only a soft-deleted job can be permanently deleted. Delete it first." },
                statusCode: 409);

        // SavedJobs rows go with it (FK ON DELETE CASCADE). The job.deleted row commits with the removal.
        db.Jobs.Remove(job);
        db.OutboxEvents.Add(JobOutboxEvents.Deleted(id));
        await db.SaveChangesAsync(ct);

        loggerFactory.CreateLogger("AdminJobEndpoints").LogWarning(
            "Admin {AdminId} permanently deleted job {JobId} ({Title}).", adminId.Value, id, job.Title);
        return Results.NoContent();
    }

    /// <summary>
    /// Same rules as the recruiter POST/PUT /api/jobs: required title, description and
    /// location, an existing company, and an existing category when one is given.
    /// </summary>
    private static async Task<(Company? Company, Category? Category, IResult? Error)> ValidateJobInputAsync(
        JobDbContext db, string? title, string? description, string? location,
        Guid companyId, Guid? categoryId, CancellationToken ct)
    {
        static IResult Invalid(string field, string message) =>
            Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

        if (string.IsNullOrWhiteSpace(title)) return (null, null, Invalid("title", "Title is required."));
        if (string.IsNullOrWhiteSpace(description)) return (null, null, Invalid("description", "Description is required."));
        if (string.IsNullOrWhiteSpace(location)) return (null, null, Invalid("location", "Location is required."));

        var company = await db.Companies.FirstOrDefaultAsync(c => c.Id == companyId, ct);
        if (company is null) return (null, null, Invalid("companyId", "Company not found."));

        Category? category = null;
        if (categoryId.HasValue)
        {
            category = await db.Categories.FirstOrDefaultAsync(c => c.Id == categoryId.Value, ct);
            if (category is null) return (null, null, Invalid("categoryId", "Category not found."));
        }

        return (company, category, null);
    }

    // ── POST /api/admin/jobs/{id}/takedown ───────────────────────────────────

    /// <summary>
    /// Takes a job down with a required reason. Status becomes Deleted (same state as a recruiter
    /// soft-delete) and the reason/actor/time audit is persisted. Idempotent: a job already taken down
    /// by moderation answers 200 without touching the stored audit.
    /// </summary>
    private static async Task<IResult> TakedownJob(
        Guid id,
        TakedownJobRequest? body,
        JobDbContext db,
        HttpContext ctx,
        ILoggerFactory loggerFactory,
        CancellationToken ct = default)
    {
        var (adminId, role) = IdentityHelper.GetIdentity(ctx);
        if (adminId is null) return UnauthorizedResult();
        if (role != "Admin") return ForbiddenResult();

        // Validate the reason before touching the DB (fail fast).
        var reason = body?.Reason?.Trim();
        if (string.IsNullOrWhiteSpace(reason))
            return Results.Json(new { message = "Takedown reason is required." }, statusCode: 422);
        if (reason.Length > JobPosting.ModerationReasonMaxLength)
            return Results.Json(
                new { message = $"Takedown reason must not exceed {JobPosting.ModerationReasonMaxLength} characters." },
                statusCode: 422);

        var job = await db.Jobs.IgnoreQueryFilters()
            .FirstOrDefaultAsync(j => j.Id == id, ct);
        if (job is null)
            return Results.NotFound(new { message = "Job not found." });

        // Idempotent: a repeated takedown keeps the original reason/actor/time and queues no second event.
        // A job soft-deleted by its recruiter has no moderation record, so takedown still applies.
        if (job.Status == JobStatus.Deleted && job.ModerationReason is not null)
            return Results.Ok(new { message = "Job already taken down.", jobId = job.Id });

        var result = job.Takedown(reason, adminId.Value);
        if (!result.IsSuccess)
            return Results.Json(new { message = result.Error }, statusCode: 422);

        // job.deleted removes the document from search-svc (idempotent there too).
        db.OutboxEvents.Add(JobOutboxEvents.Deleted(job.Id));
        await db.SaveChangesAsync(ct);

        loggerFactory.CreateLogger("AdminJobEndpoints").LogWarning(
            "Admin {AdminId} took down job {JobId}. Reason={Reason}", adminId.Value, job.Id, job.ModerationReason);
        return Results.Ok(new { message = "Job taken down.", jobId = job.Id });
    }

    // ── POST /api/admin/jobs/{id}/restore ────────────────────────────────────

    /// <summary>
    /// Restores a job that was taken down by moderation: Active again, audit cleared, and a job.updated
    /// event re-indexes it (search-svc upserts Active documents on job.updated).
    /// A job deleted by its recruiter has no moderation record and is not restorable (409).
    /// </summary>
    private static async Task<IResult> RestoreJob(
        Guid id,
        JobDbContext db,
        HttpContext ctx,
        ILoggerFactory loggerFactory,
        CancellationToken ct = default)
    {
        var (adminId, role) = IdentityHelper.GetIdentity(ctx);
        if (adminId is null) return UnauthorizedResult();
        if (role != "Admin") return ForbiddenResult();

        var job = await db.Jobs.IgnoreQueryFilters()
            .Include(j => j.Company)
            .Include(j => j.Category)
            .FirstOrDefaultAsync(j => j.Id == id, ct);
        if (job is null)
            return Results.NotFound(new { message = "Job not found." });

        var result = job.Restore();
        if (!result.IsSuccess)
            return Results.Json(new { message = result.Error }, statusCode: 409);

        // job.updated with status Active: search-svc re-indexes the document.
        db.OutboxEvents.Add(JobOutboxEvents.Updated(job, job.Company?.Name ?? "", job.Category?.Name));
        await db.SaveChangesAsync(ct);

        loggerFactory.CreateLogger("AdminJobEndpoints").LogInformation(
            "Admin {AdminId} restored job {JobId}.", adminId.Value, job.Id);
        return Results.Ok(new { message = "Job restored.", jobId = job.Id });
    }
}

// ── Request / Response DTOs ───────────────────────────────────────────────────

/// <summary>Request body for a moderation takedown; the reason is required.</summary>
public record TakedownJobRequest(string? Reason);

/// <summary>Admin job list item DTO (GET /api/admin/jobs).</summary>
public record JobAdminListDto(
    Guid Id,
    string Title,
    string Status,
    Guid CompanyId,
    string? CompanyName,
    Guid RecruiterId,
    string? CategoryName,
    DateTime CreatedAt,
    string Location,
    DateTime UpdatedAt,
    string? ModerationReason,
    Guid? ModeratedBy,
    DateTime? ModeratedAt)
{
    public static JobAdminListDto From(JobPosting job) => new(
        job.Id, job.Title, job.Status.ToString(),
        job.CompanyId, job.Company?.Name,
        job.RecruiterId, job.Category?.Name,
        job.CreatedAt, job.Location, job.UpdatedAt,
        job.ModerationReason, job.ModeratedBy, job.ModeratedAt);
}

/// <summary>
/// Admin job detail DTO (GET /api/admin/jobs/{id}) — full fields plus the moderation audit.
/// Legacy rows converted from the removed approval flow (Rejected -&gt; Closed) carry their reason here.
/// </summary>
public record JobAdminDetailDto(
    Guid Id,
    string Title,
    string Description,
    string Status,
    Guid CompanyId,
    string? CompanyName,
    string Location,
    decimal? SalaryMin,
    decimal? SalaryMax,
    string SalaryCurrency,
    Guid? CategoryId,
    string? CategoryName,
    string? Requirements,
    string? Benefits,
    string EmploymentType,
    string ExperienceLevel,
    Guid RecruiterId,
    int ViewCount,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    // Moderation fields (takedown reason, actor and time)
    string? ModerationReason,
    Guid? ModeratedBy,
    DateTime? ModeratedAt)
{
    public static JobAdminDetailDto From(JobPosting job) => new(
        job.Id, job.Title, job.Description, job.Status.ToString(),
        job.CompanyId, job.Company?.Name,
        job.Location, job.SalaryMin, job.SalaryMax, job.SalaryCurrency,
        job.CategoryId, job.Category?.Name,
        job.Requirements, job.Benefits,
        job.EmploymentType, job.ExperienceLevel,
        job.RecruiterId, job.ViewCount,
        job.CreatedAt, job.UpdatedAt,
        job.ModerationReason, job.ModeratedBy, job.ModeratedAt);
}
