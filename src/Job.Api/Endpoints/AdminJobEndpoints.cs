using System.Text.Json;
using Job.Api.Auth;
using Job.Api.DTOs;
using Job.Core.Entities;
using Job.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using SharedKernel.Events;
using SharedKernel.Kafka;
using JobPosting = Job.Core.Entities.Job;

namespace Job.Api.Endpoints;

/// <summary>
/// Admin-only job management endpoints (feat(admin): Slice 4).
/// GET  /api/admin/jobs          — paginated job list (all statuses)
/// GET  /api/admin/jobs/{id}     — full job detail
/// POST /api/admin/jobs/{id}/approve  — approve a Pending job
/// POST /api/admin/jobs/{id}/reject   — reject a Pending job with reason
/// All endpoints require X-User-Role: Admin.
/// Uses IgnoreQueryFilters() so Admin can see Pending and Rejected jobs.
/// Approve/Reject write OutboxEvent atomically in the same SaveChangesAsync call.
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
        group.MapPost("/{id:guid}/approve", ApproveJob);
        group.MapPost("/{id:guid}/reject", RejectJob);
        return app;
    }

    // ── GET /api/admin/jobs ───────────────────────────────────────────────────

    private static async Task<IResult> GetAdminJobs(
        JobDbContext db,
        HttpContext ctx,
        string? status = null,
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

        // Use job-svc PaginatedResponse<T> (consistent with other job-svc endpoints).
        return Results.Ok(new PaginatedResponse<JobAdminListDto>(dtos, total, page, size, totalPages));
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
            job.Status.ToString(), DateTime.UtcNow);

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
    DateTime CreatedAt)
{
    public static JobAdminListDto From(JobPosting job) => new(
        job.Id, job.Title, job.Status.ToString(),
        job.CompanyId, job.Company?.Name,
        job.RecruiterId, job.Category?.Name,
        job.CreatedAt);
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
