using Job.Api.Auth;
using Job.Core.Entities;
using Job.Core.InviteCodes;
using Job.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Job.Api.Endpoints;

/// <summary>
/// SRS AUTH-01-09 recruiter invite codes.
/// <list type="bullet">
/// <item><c>POST /api/companies/{id}/invite-codes</c>: public route, Recruiter only, company creator only. Plain code returned once.</item>
/// <item><c>/api/internal/invite-codes/*</c>: auth-svc only. Bearer <c>JOB_INTERNAL_TOKEN_FOR_AUTH</c>, fails closed (401). Not gateway-routed.</item>
/// </list>
/// Every bad code (malformed, unknown, expired, used) returns the same <c>INVITE_CODE_INVALID</c> 422.
/// </summary>
public static class InviteCodeEndpoints
{
    public const string InvalidCodeError = "INVITE_CODE_INVALID";
    public const string NotCreatorError = "INVITE_CODE_NOT_CREATOR";
    public const string CompanyNotFoundError = "COMPANY_NOT_FOUND";
    public const string TtlConfigKey = "INVITE_CODE_TTL_DAYS";
    public const int DefaultTtlDays = 7;

    public static WebApplication MapInviteCodeEndpoints(this WebApplication app)
    {
        app.MapPost("/api/companies/{id:guid}/invite-codes", IssueInviteCodeAsync)
            .WithTags("Companies")
            .ExcludeFromDescription();

        var internalGroup = app.MapGroup("/api/internal/invite-codes").WithTags("Internal").ExcludeFromDescription();
        internalGroup.MapPost("/check", CheckInviteCodeAsync);
        internalGroup.MapPost("/redeem", RedeemInviteCodeAsync);
        internalGroup.MapPost("/{id:guid}/release", ReleaseInviteCodeAsync);
        return app;
    }

    public sealed record IssuedInviteCodeResponse(Guid Id, string Code, DateTimeOffset ExpiresAt);

    public sealed record InviteCodeRequest(string? Code);

    public sealed record InviteCodeCheckResponse(Guid CompanyId, string CompanyName);

    public sealed record InviteCodeRedeemResponse(Guid InviteCodeId, Guid CompanyId);

    public static async Task<IResult> IssueInviteCodeAsync(
        Guid id,
        HttpContext ctx,
        JobDbContext db,
        IConfiguration config,
        CancellationToken ct = default)
    {
        var (userId, role) = IdentityHelper.GetIdentity(ctx);
        if (userId is null)
            return Json(401, "Unauthorized", "Unauthorized. Missing or invalid user identity.");
        if (role != "Recruiter")
            return Json(403, "Forbidden", "Forbidden. Recruiter role required.");

        var company = await db.Companies.AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => new { c.Id, c.CreatedBy })
            .FirstOrDefaultAsync(ct);
        if (company is null)
            return Json(404, CompanyNotFoundError, "Company not found.");

        // Empty CreatedBy = legacy row from the AddCompanyCreatedBy migration: nobody may issue for it.
        if (company.CreatedBy == Guid.Empty || company.CreatedBy != userId.Value)
            return Json(403, NotCreatorError, "Only the company creator can issue invite codes.");

        var ttlDays = ReadTtlDays(config);
        var plain = InviteCodeFormat.Generate();
        var entity = new InviteCode(
            company.Id,
            InviteCodeFormat.Hash(plain),
            userId.Value,
            DateTimeOffset.UtcNow,
            TimeSpan.FromDays(ttlDays));

        db.InviteCodes.Add(entity);
        await db.SaveChangesAsync(ct);

        // The plain code leaves this process once, in this response. Never log it.
        return Results.Created(
            $"/api/companies/{company.Id}/invite-codes/{entity.Id}",
            new IssuedInviteCodeResponse(entity.Id, InviteCodeFormat.Display(plain), entity.ExpiresAt));
    }

    public static async Task<IResult> CheckInviteCodeAsync(
        InviteCodeRequest request,
        HttpContext ctx,
        JobDbContext db,
        IConfiguration config,
        CancellationToken ct = default)
    {
        if (!IsInternalCallerAuthorized(ctx, config))
            return Unauthorized();

        var hash = NormalizeToHash(request.Code);
        if (hash is null)
            return InvalidCode();

        var now = DateTimeOffset.UtcNow;
        var row = await db.InviteCodes.AsNoTracking()
            .Where(c => c.CodeHash == hash && c.UsedAt == null && c.ExpiresAt > now)
            .Join(db.Companies.AsNoTracking(), c => c.CompanyId, co => co.Id,
                (c, co) => new { CompanyId = co.Id, CompanyName = co.Name })
            .FirstOrDefaultAsync(ct);

        return row is null
            ? InvalidCode()
            : Results.Ok(new InviteCodeCheckResponse(row.CompanyId, row.CompanyName));
    }

    /// <summary>
    /// Atomic single use: one conditional UPDATE. Only one concurrent caller can match
    /// <c>used_at IS NULL</c>. The in-memory provider (unit tests) cannot run ExecuteUpdateAsync,
    /// so it falls back to a tracked update, as JobEndpoints does for view counts.
    /// </summary>
    public static async Task<IResult> RedeemInviteCodeAsync(
        InviteCodeRequest request,
        HttpContext ctx,
        JobDbContext db,
        IConfiguration config,
        CancellationToken ct = default)
    {
        if (!IsInternalCallerAuthorized(ctx, config))
            return Unauthorized();

        var hash = NormalizeToHash(request.Code);
        if (hash is null)
            return InvalidCode();

        var now = DateTimeOffset.UtcNow;
        var claimed = await db.InviteCodes.AsNoTracking()
            .Where(c => c.CodeHash == hash && c.UsedAt == null && c.ExpiresAt > now)
            .Select(c => c.Id)
            .FirstOrDefaultAsync(ct);
        if (claimed == Guid.Empty)
            return InvalidCode();

        int updated;
        if (db.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")
        {
            var tracked = await db.InviteCodes.FirstAsync(c => c.Id == claimed, ct);
            updated = tracked.UsedAt is null ? 1 : 0;
            if (updated == 1)
            {
                tracked.MarkUsed(now);
                await db.SaveChangesAsync(ct);
            }
        }
        else
        {
            updated = await db.InviteCodes
                .Where(c => c.CodeHash == hash && c.UsedAt == null && c.ExpiresAt > now)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.UsedAt, now), ct);
        }

        if (updated == 0)
            return InvalidCode();

        // Safe to read back: the update above succeeded for exactly one caller.
        var redeemed = await db.InviteCodes.AsNoTracking()
            .Where(c => c.Id == claimed)
            .Select(c => new { c.Id, c.CompanyId })
            .FirstAsync(ct);
        return Results.Ok(new InviteCodeRedeemResponse(redeemed.Id, redeemed.CompanyId));
    }

    /// <summary>Clears <c>used_at</c> after auth-svc fails to create the user. Idempotent.</summary>
    public static async Task<IResult> ReleaseInviteCodeAsync(
        Guid id,
        HttpContext ctx,
        JobDbContext db,
        IConfiguration config,
        CancellationToken ct = default)
    {
        if (!IsInternalCallerAuthorized(ctx, config))
            return Unauthorized();

        if (db.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")
        {
            var tracked = await db.InviteCodes.FirstOrDefaultAsync(c => c.Id == id, ct);
            if (tracked is not null)
            {
                tracked.ClearUsed();
                await db.SaveChangesAsync(ct);
            }
        }
        else
        {
            await db.InviteCodes.Where(c => c.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.UsedAt, (DateTimeOffset?)null), ct);
        }
        return Results.NoContent();
    }

    public static int ReadTtlDays(IConfiguration config)
    {
        var raw = config[TtlConfigKey];
        return int.TryParse(raw, out var days) && days > 0 ? days : DefaultTtlDays;
    }

    private static bool IsInternalCallerAuthorized(HttpContext ctx, IConfiguration config)
    {
        var expected = config[InternalStatsEndpoints.AuthCallerTokenKey];
        return !string.IsNullOrWhiteSpace(expected) &&
               InternalStatsEndpoints.IsAuthorized(ctx.Request.Headers.Authorization.ToString(), expected);
    }

    private static string? NormalizeToHash(string? raw)
    {
        var normalized = InviteCodeFormat.Normalize(raw);
        return normalized is null ? null : InviteCodeFormat.Hash(normalized);
    }

    private static IResult InvalidCode() =>
        Json(422, InvalidCodeError, "The invite code is invalid or has expired.");

    private static IResult Unauthorized() =>
        Results.Json(new { message = "Unauthorized" }, statusCode: 401);

    private static IResult Json(int status, string errorCode, string message) =>
        Results.Json(new { errorCode, message }, statusCode: status);
}
