using Job.Api.Auth;
using Job.Core.Entities;
using Job.Infrastructure.Clients;
using Job.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Job.Api.Endpoints;

/// <summary>
/// Public join-request endpoints (SRS AUTH-01-12 to AUTH-01-14). Identity comes from the gateway headers.
/// <list type="bullet">
/// <item><c>GET /api/companies/{id}/join-requests</c>: Company admin lists requests.</item>
/// <item><c>POST /api/companies/{id}/join-requests/{requestId}/approve|reject</c>: Company admin decides.</item>
/// <item><c>GET /api/companies/me/join-request</c>: requester sees status and expiry.</item>
/// <item><c>POST /api/companies/me/join-request/escalate</c>: requester escalates once after expiry.</item>
/// </list>
/// </summary>
public static class CompanyJoinRequestEndpoints
{
    public const string NotAdminError = "JOIN_REQUEST_NOT_ADMIN";
    public const string NotFoundError = "JOIN_REQUEST_NOT_FOUND";
    public const string StateInvalidError = "JOIN_REQUEST_STATE_INVALID";
    public const string CompanyNotFoundError = "COMPANY_NOT_FOUND";
    public const string LinkUnavailableError = "AUTH_LINK_UNAVAILABLE";
    public const string AccountAlreadyHasCompanyError = CompanyEndpoints.AccountAlreadyHasCompanyError;

    public static WebApplication MapCompanyJoinRequestEndpoints(this WebApplication app)
    {
        app.MapGet("/api/companies/{id:guid}/join-requests", ListJoinRequests)
            .WithTags("Companies")
            .ExcludeFromDescription();
        app.MapPost("/api/companies/{id:guid}/join-requests/{requestId:guid}/approve", ApproveJoinRequest)
            .WithTags("Companies")
            .ExcludeFromDescription();
        app.MapPost("/api/companies/{id:guid}/join-requests/{requestId:guid}/reject", RejectJoinRequest)
            .WithTags("Companies")
            .ExcludeFromDescription();
        app.MapGet("/api/companies/me/join-request", GetMyJoinRequest)
            .WithTags("Companies")
            .ExcludeFromDescription();
        app.MapPost("/api/companies/me/join-request/escalate", EscalateMyJoinRequest)
            .WithTags("Companies")
            .ExcludeFromDescription();
        return app;
    }

    public sealed record RejectJoinRequestBody(string? Reason);

    public sealed record JoinRequestView(
        Guid Id,
        Guid CompanyId,
        Guid UserId,
        string Status,
        DateTimeOffset ExpiresAt,
        DateTimeOffset CreatedAt,
        DateTimeOffset? DecidedAt,
        string? DecisionReason);

    public sealed record MyJoinRequestView(
        Guid Id,
        Guid CompanyId,
        string Status,
        DateTimeOffset ExpiresAt,
        DateTimeOffset CreatedAt,
        bool CanEscalate);

    public sealed record JoinRequestPage(IReadOnlyList<JoinRequestView> Items, int Total, int Page, int Size);

    public static async Task<IResult> ListJoinRequests(
        Guid id,
        HttpContext ctx,
        JobDbContext db,
        int page = 1,
        int size = 20,
        CancellationToken ct = default)
    {
        var (userId, role) = IdentityHelper.GetIdentity(ctx);
        if (userId is null)
            return ApiErrors.Error(401, "UNAUTHORIZED", "Unauthorized. Missing or invalid user identity.");
        var admin = await CheckAdminAsync(id, userId.Value, role, db, ct);
        if (admin is not null)
            return admin;

        size = Math.Clamp(size <= 0 ? 20 : size, 1, 100);
        page = Math.Max(1, page);

        var query = db.CompanyJoinRequests.AsNoTracking().Where(r => r.CompanyId == id);
        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(r => r.CreatedAt)
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;
        var items = rows.Select(r => new JoinRequestView(
            r.Id, r.CompanyId, r.UserId, r.EffectiveStatus(now).ToString(), r.ExpiresAt,
            new DateTimeOffset(r.CreatedAt, TimeSpan.Zero), r.DecidedAt, r.DecisionReason)).ToList();
        return Results.Ok(new JoinRequestPage(items, total, page, size));
    }

    /// <summary>
    /// Admin approval (AUTH-01-12). The request becomes Approved only after auth-svc links the account. On an
    /// auth-svc failure it stays Pending, and the admin may retry.
    /// </summary>
    public static async Task<IResult> ApproveJoinRequest(
        Guid id,
        Guid requestId,
        HttpContext ctx,
        JobDbContext db,
        IAuthCompanyLinker linker,
        CancellationToken ct)
    {
        var (userId, role) = IdentityHelper.GetIdentity(ctx);
        if (userId is null)
            return ApiErrors.Error(401, "UNAUTHORIZED", "Unauthorized. Missing or invalid user identity.");
        var admin = await CheckAdminAsync(id, userId.Value, role, db, ct);
        if (admin is not null)
            return admin;

        var joinRequest = await db.CompanyJoinRequests.FirstOrDefaultAsync(r => r.Id == requestId && r.CompanyId == id, ct);
        if (joinRequest is null)
            return ApiErrors.Error(404, NotFoundError, "Join request not found.");

        var now = DateTimeOffset.UtcNow;
        if (joinRequest.EffectiveStatus(now) != CompanyJoinRequestStatus.Pending)
            return ApiErrors.Error(409, StateInvalidError, "Only a pending, unexpired join request can be approved.");

        var link = await linker.LinkCompanyAsync(joinRequest.UserId, id, ct);
        if (link == LinkCompanyResult.AlreadyHasCompany)
            return ApiErrors.Error(409, AccountAlreadyHasCompanyError, "The requester already belongs to another company.");
        if (link == LinkCompanyResult.Unavailable)
            return ApiErrors.Error(503, LinkUnavailableError, "The account service could not link the company. Retry later.");

        if (joinRequest.EffectiveStatus(DateTimeOffset.UtcNow) != CompanyJoinRequestStatus.Pending)
            return ApiErrors.Error(409, StateInvalidError, "The join request is no longer pending.");
        joinRequest.Approve(userId.Value, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { id = joinRequest.Id, status = joinRequest.Status.ToString() });
    }

    public static async Task<IResult> RejectJoinRequest(
        Guid id,
        Guid requestId,
        RejectJoinRequestBody? body,
        HttpContext ctx,
        JobDbContext db,
        CancellationToken ct)
    {
        var (userId, role) = IdentityHelper.GetIdentity(ctx);
        if (userId is null)
            return ApiErrors.Error(401, "UNAUTHORIZED", "Unauthorized. Missing or invalid user identity.");
        var admin = await CheckAdminAsync(id, userId.Value, role, db, ct);
        if (admin is not null)
            return admin;

        var joinRequest = await db.CompanyJoinRequests.FirstOrDefaultAsync(r => r.Id == requestId && r.CompanyId == id, ct);
        if (joinRequest is null)
            return ApiErrors.Error(404, NotFoundError, "Join request not found.");

        var now = DateTimeOffset.UtcNow;
        if (joinRequest.EffectiveStatus(now) != CompanyJoinRequestStatus.Pending)
            return ApiErrors.Error(409, StateInvalidError, "Only a pending, unexpired join request can be rejected.");

        joinRequest.Reject(userId.Value, now, body?.Reason);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { id = joinRequest.Id, status = joinRequest.Status.ToString() });
    }

    public static async Task<IResult> GetMyJoinRequest(HttpContext ctx, JobDbContext db, CancellationToken ct)
    {
        var (userId, _) = IdentityHelper.GetIdentity(ctx);
        if (userId is null)
            return ApiErrors.Error(401, "UNAUTHORIZED", "Unauthorized. Missing or invalid user identity.");

        var joinRequest = await db.CompanyJoinRequests.AsNoTracking()
            .FirstOrDefaultAsync(r => r.UserId == userId.Value, ct);
        if (joinRequest is null)
            return ApiErrors.Error(404, NotFoundError, "No join request for this account.");

        var now = DateTimeOffset.UtcNow;
        var effective = joinRequest.EffectiveStatus(now);
        return Results.Ok(new MyJoinRequestView(
            joinRequest.Id,
            joinRequest.CompanyId,
            effective.ToString(),
            joinRequest.ExpiresAt,
            new DateTimeOffset(joinRequest.CreatedAt, TimeSpan.Zero),
            CanEscalate: effective == CompanyJoinRequestStatus.Expired));
    }

    /// <summary>Escalation to support (AUTH-01-14). Allowed once, only when the effective status is Expired.</summary>
    public static async Task<IResult> EscalateMyJoinRequest(HttpContext ctx, JobDbContext db, CancellationToken ct)
    {
        var (userId, _) = IdentityHelper.GetIdentity(ctx);
        if (userId is null)
            return ApiErrors.Error(401, "UNAUTHORIZED", "Unauthorized. Missing or invalid user identity.");

        var joinRequest = await db.CompanyJoinRequests.FirstOrDefaultAsync(r => r.UserId == userId.Value, ct);
        if (joinRequest is null)
            return ApiErrors.Error(404, NotFoundError, "No join request for this account.");

        var now = DateTimeOffset.UtcNow;
        if (joinRequest.EffectiveStatus(now) != CompanyJoinRequestStatus.Expired)
            return ApiErrors.Error(409, StateInvalidError, "Only an expired join request can be escalated.");

        joinRequest.Escalate(now);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { id = joinRequest.Id, status = joinRequest.Status.ToString() });
    }

    /// <summary>Returns an error result unless the caller is a Recruiter and the Company's admin.</summary>
    private static async Task<IResult?> CheckAdminAsync(Guid companyId, Guid userId, string? role, JobDbContext db, CancellationToken ct)
    {
        var createdBy = await db.Companies.AsNoTracking()
            .Where(c => c.Id == companyId)
            .Select(c => (Guid?)c.CreatedBy)
            .FirstOrDefaultAsync(ct);
        if (createdBy is null)
            return ApiErrors.Error(404, CompanyNotFoundError, "Company not found.");
        if (role != "Recruiter" || createdBy == Guid.Empty || createdBy != userId)
            return ApiErrors.Error(403, NotAdminError, "Only the Company admin can manage join requests.");
        return null;
    }
}
