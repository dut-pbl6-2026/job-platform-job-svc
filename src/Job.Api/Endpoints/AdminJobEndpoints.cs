using System.Text.Json;
using Job.Api.Auth;
using Job.Api.DTOs;
using Job.Api.Services;
using Job.Core.Entities;
using Job.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using SharedKernel.Events;
using SharedKernel.Kafka;
using JobPosting = Job.Core.Entities.Job;

namespace Job.Api.Endpoints;

/// <summary>
/// Admin-only job management endpoints (feat(admin): Slice 4).
/// GET    /api/admin/jobs                  — paginated job list (all statuses, optional title search q)
/// GET    /api/admin/jobs/{id}             — full job detail
/// POST   /api/admin/jobs                  — create a job (Active, posted by the admin)
/// PUT    /api/admin/jobs/{id}             — update any job that is not soft-deleted
/// DELETE /api/admin/jobs/{id}             — soft delete (idempotent)
/// DELETE /api/admin/jobs/{id}/permanent   — hard delete a soft-deleted job from the DB
/// POST   /api/admin/jobs/{id}/approve     — approve a Pending job
/// POST   /api/admin/jobs/{id}/reject      — reject a Pending job with reason
/// All endpoints require X-User-Role: Admin.
/// Uses IgnoreQueryFilters() so Admin can see Pending and Rejected jobs.
/// Approve/Reject write OutboxEvent atomically in the same SaveChangesAsync call.
/// Create/update/delete publish through <see cref="KafkaJobEventPublisher"/> like the
/// recruiter endpoints (Kafka primary, HTTP fallback), so search-svc follows the change.
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
        group.MapPost("/{id:guid}/approve", ApproveJob);
        group.MapPost("/{id:guid}/reject", RejectJob);
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

        // IgnoreQueryFilters: Admin can see all statuses including Pending/Rejected/Deleted
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
        KafkaJobEventPublisher kafkaPublisher,
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
        await db.SaveChangesAsync(ct);

        var createdEvent = new JobCreatedEvent(
            job.Id, job.Title, job.Description, job.CompanyId, company!.Name,
            job.Location, job.SalaryMin, job.SalaryMax, job.SalaryCurrency,
            job.CategoryId, category?.Name, job.EmploymentType, job.ExperienceLevel,
            job.RecruiterId, job.Requirements, job.Benefits,
            job.Status.ToString(), DateTime.UtcNow, job.CreatedAt);
        // Detached from request cancellation: the job is already persisted.
        await kafkaPublisher.PublishJobCreatedAsync(
            createdEvent, company.Name, category?.Name, job.Status.ToString(), CancellationToken.None);

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
        KafkaJobEventPublisher kafkaPublisher,
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

        await db.SaveChangesAsync(ct);

        // Update() never changes the status: publish the actual one so a Pending or
        // Closed job edited here stays out of the public index.
        var updatedEvent = new JobUpdatedEvent(
            job.Id, job.Title, job.Description, job.CompanyId, company!.Name,
            job.Location, job.SalaryMin, job.SalaryMax, job.SalaryCurrency,
            job.CategoryId, category?.Name, job.EmploymentType, job.ExperienceLevel,
            job.RecruiterId, job.Requirements, job.Benefits,
            job.Status.ToString(), DateTime.UtcNow, job.CreatedAt);
        await kafkaPublisher.PublishJobUpdatedAsync(
            updatedEvent, company.Name, category?.Name, job.Status.ToString(), CancellationToken.None);

        loggerFactory.CreateLogger("AdminJobEndpoints").LogInformation(
            "Admin {AdminId} updated job {JobId}.", adminId.Value, job.Id);
        return Results.Ok(new { message = "Job updated." });
    }

    // ── DELETE /api/admin/jobs/{id} ───────────────────────────────────────────

    private static async Task<IResult> SoftDeleteJob(
        Guid id,
        JobDbContext db,
        HttpContext ctx,
        KafkaJobEventPublisher kafkaPublisher,
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
        {
            job.SoftDelete();
            await db.SaveChangesAsync(ct);
        }

        // Published even when the job was already deleted: idempotent on search-svc,
        // and it removes a document left in the index by an earlier lost event.
        await kafkaPublisher.PublishJobDeletedAsync(job.Id, CancellationToken.None);

        loggerFactory.CreateLogger("AdminJobEndpoints").LogInformation(
            "Admin {AdminId} soft-deleted job {JobId}.", adminId.Value, job.Id);
        return Results.NoContent();
    }

    // ── DELETE /api/admin/jobs/{id}/permanent ─────────────────────────────────

    private static async Task<IResult> HardDeleteJob(
        Guid id,
        JobDbContext db,
        HttpContext ctx,
        KafkaJobEventPublisher kafkaPublisher,
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

        // SavedJobs rows go with it (FK ON DELETE CASCADE). Queued outbox rows for this
        // job are left alone: the publisher sends job.deleted for a job that no longer exists.
        db.Jobs.Remove(job);
        await db.SaveChangesAsync(ct);
        await kafkaPublisher.PublishJobDeletedAsync(id, CancellationToken.None);

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

    // ── POST /api/admin/jobs/{id}/approve ─────────────────────────────────────

    private static async Task<IResult> ApproveJob(
        Guid id,
        ApproveJobRequest? body,
        JobDbContext db,
        HttpContext ctx,
        ILoggerFactory loggerFactory,
        CancellationToken ct = default)
    {
        var (adminId, role) = IdentityHelper.GetIdentity(ctx);
        if (adminId is null) return UnauthorizedResult();
        if (role != "Admin") return ForbiddenResult();

        // IgnoreQueryFilters: find Pending jobs hidden from public query filter
        var job = await db.Jobs.IgnoreQueryFilters()
            .Include(j => j.Company)
            .Include(j => j.Category)
            .FirstOrDefaultAsync(j => j.Id == id, ct);

        if (job is null)
            return Results.NotFound(new { message = "Job not found." });

        var approveResult = job.ApprovePending(adminId.Value);
        if (!approveResult.IsSuccess)
            return Results.Json(new { message = approveResult.Error }, statusCode: 400);

        // ATOMIC: job update + outbox event in one SaveChanges (single DB transaction)
        db.OutboxEvents.Add(BuildUpdatedOutboxEvent(job));

        await db.SaveChangesAsync(ct);

        loggerFactory.CreateLogger("AdminJobEndpoints").LogInformation(
            "Admin {AdminId} approved job {JobId}. Note={Note}",
            adminId.Value, job.Id, body?.Note);
        return Results.Ok(new { message = "Job approved.", jobId = job.Id });
    }

    // ── POST /api/admin/jobs/{id}/reject ──────────────────────────────────────

    private static async Task<IResult> RejectJob(
        Guid id,
        RejectJobRequest? body,
        JobDbContext db,
        HttpContext ctx,
        ILoggerFactory loggerFactory,
        CancellationToken ct = default)
    {
        var (adminId, role) = IdentityHelper.GetIdentity(ctx);
        if (adminId is null) return UnauthorizedResult();
        if (role != "Admin") return ForbiddenResult();

        // Validate reason input BEFORE touching DB (fail-fast)
        var reason = body?.Reason?.Trim();
        if (string.IsNullOrWhiteSpace(reason))
            return Results.Json(new { message = "Rejection reason is required." }, statusCode: 422);
        if (reason.Length > 500)
            return Results.Json(new { message = "Rejection reason must not exceed 500 characters." }, statusCode: 422);

        var job = await db.Jobs.IgnoreQueryFilters()
            .Include(j => j.Company)
            .Include(j => j.Category)
            .FirstOrDefaultAsync(j => j.Id == id, ct);

        if (job is null)
            return Results.NotFound(new { message = "Job not found." });

        var rejectResult = job.RejectPending(reason, adminId.Value);
        if (!rejectResult.IsSuccess)
            return Results.Json(new { message = rejectResult.Error }, statusCode: 400);

        // ATOMIC: job update + outbox event in one SaveChanges (single DB transaction)
        db.OutboxEvents.Add(BuildUpdatedOutboxEvent(job));

        await db.SaveChangesAsync(ct);

        loggerFactory.CreateLogger("AdminJobEndpoints").LogInformation(
            "Admin {AdminId} rejected job {JobId}.",
            adminId.Value, job.Id);
        return Results.Ok(new { message = "Job rejected.", jobId = job.Id });
    }

    /// <summary>
    /// Builds the <c>job.updated</c> outbox record for a job whose status just changed.
    /// The payload is serialized with <see cref="KafkaJson.Options"/> so the stored
    /// bytes match what <c>KafkaProducerService</c> writes on the wire.
    /// </summary>
    private static OutboxEvent BuildUpdatedOutboxEvent(JobPosting job)
    {
        var updatedEvent = new JobUpdatedEvent(
            job.Id, job.Title, job.Description, job.CompanyId, job.Company?.Name ?? "",
            job.Location, job.SalaryMin, job.SalaryMax, job.SalaryCurrency,
            job.CategoryId, job.Category?.Name, job.EmploymentType, job.ExperienceLevel,
            job.RecruiterId, job.Requirements, job.Benefits,
            job.Status.ToString(), DateTime.UtcNow, job.CreatedAt);

        return new OutboxEvent(
            eventType: JobEventTypes.Updated,
            payload: JsonSerializer.Serialize(
                EventEnvelope<JobUpdatedEvent>.Create(JobEventTypes.Updated, updatedEvent),
                KafkaJson.Options),
            aggregateId: job.Id.ToString());
    }
}

// ── Request / Response DTOs ───────────────────────────────────────────────────

/// <summary>Request body for job approval.</summary>
public record ApproveJobRequest(string? Note);

/// <summary>Request body for job rejection.</summary>
public record RejectJobRequest(string? Reason);

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
    DateTime UpdatedAt)
{
    public static JobAdminListDto From(JobPosting job) => new(
        job.Id, job.Title, job.Status.ToString(),
        job.CompanyId, job.Company?.Name,
        job.RecruiterId, job.Category?.Name,
        job.CreatedAt, job.Location, job.UpdatedAt);
}

/// <summary>Admin job detail DTO (GET /api/admin/jobs/{id}) — full fields including rejection info.</summary>
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
    // Rejection fields
    string? RejectionReason,
    DateTime? RejectedAt,
    Guid? RejectedBy,
    // Approval fields
    DateTime? ApprovedAt,
    Guid? ApprovedBy)
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
        job.RejectionReason, job.RejectedAt, job.RejectedBy,
        job.ApprovedAt, job.ApprovedBy);
}
