using Job.Api.Auth;
using Job.Core.Companies;
using Job.Core.Entities;
using Job.Infrastructure.Clients;
using Job.Infrastructure.Companies;
using Job.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Job.Api.Endpoints;

/// <summary>
/// Internal company endpoints for auth-svc company sign-up and join requests (SRS AUTH-01-10 to AUTH-01-14).
/// All routes require the bearer <c>JOB_INTERNAL_TOKEN_FOR_AUTH</c> (fail-closed, 401) and are not gateway-routed.
/// The support-decision route uses the same token by owner decision (see the PR notes).
/// </summary>
public static class InternalCompanyEndpoints
{
    public const string DomainBlockedError = "WORK_EMAIL_REQUIRED";
    public const string CompanyInvalidError = "COMPANY_INVALID";
    public const string DomainTakenError = "COMPANY_DOMAIN_TAKEN";
    public const string NameTakenError = "COMPANY_NAME_TAKEN";
    public const string TaxCodeTakenError = "COMPANY_TAX_CODE_TAKEN";
    public const string AccountAlreadyHasCompanyError = CompanyEndpoints.AccountAlreadyHasCompanyError;
    public const string JoinRequestStateInvalidError = "JOIN_REQUEST_STATE_INVALID";
    public const string JoinRequestExistsError = "JOIN_REQUEST_EXISTS";
    public const string JoinRequestNotFoundError = "JOIN_REQUEST_NOT_FOUND";
    public const string CompanyNotVerifiedError = "COMPANY_NOT_VERIFIED";
    public const string LinkUnavailableError = "AUTH_LINK_UNAVAILABLE";
    public const string SupportDecisionInvalidError = "SUPPORT_DECISION_INVALID";

    public static WebApplication MapInternalCompanyEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/internal").WithTags("Internal").ExcludeFromDescription();
        group.MapPost("/companies/availability", CheckAvailabilityAsync);
        group.MapPost("/companies/domain-match", DomainMatchAsync);
        group.MapPost("/companies", CreateCompanyForFounderAsync);
        group.MapPost("/company-join-requests", CreateJoinRequestAsync);
        group.MapPost("/company-join-requests/{id:guid}/support-decision", SupportDecisionAsync);
        return app;
    }

    public sealed record AvailabilityRequest(string? CompanyName, string? TaxCode);

    public sealed record AvailabilityResponse(bool Available);

    public sealed record DomainMatchRequest(string? Domain);

    public sealed record DomainMatchResponse(Guid? CompanyId);

    public sealed record CreateCompanyRequest(string? Name, string? TaxCode, Guid CreatedBy, string? EmailDomain);

    // The property name is the contract: auth-svc deserialises { companyId } (SRS Appendix D) and
    // would otherwise bind an all-zero Guid and link the founder to no company.
    public sealed record CreateCompanyResponse(Guid CompanyId, string EmailDomain, bool Verified);

    public sealed record CreateJoinRequestRequest(Guid UserId, Guid CompanyId);

    // auth-svc calls this during verification; the SRS names the identifier { joinRequestId }.
    public sealed record JoinRequestResponse(Guid JoinRequestId, Guid CompanyId, Guid UserId, string Status, DateTimeOffset ExpiresAt);

    public sealed record SupportDecisionRequest(string? Decision, string? Reason, string? Operator);

    /// <summary>POST /api/internal/companies/availability: name and tax code are free under the public create rules.</summary>
    public static async Task<IResult> CheckAvailabilityAsync(
        AvailabilityRequest request,
        HttpContext ctx,
        JobDbContext db,
        IConfiguration config,
        CancellationToken ct)
    {
        if (!InternalCallerAuth.IsAuthCallerAuthorized(ctx, config))
            return InternalCallerAuth.Unauthorized();

        var name = request.CompanyName?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > Company.NameMaxLength)
            return ApiErrors.Error(422, CompanyInvalidError, "companyName is required and must be within the length limit.");

        var conflict = await CompanyUniqueness.FindConflictAsync(
            db, name, request.TaxCode, emailDomain: null, ct);
        return Results.Ok(new AvailabilityResponse(conflict is CompanyConflict.None));
    }

    /// <summary>POST /api/internal/companies/domain-match: the verified Company holding the domain, or null.</summary>
    public static async Task<IResult> DomainMatchAsync(
        DomainMatchRequest request,
        HttpContext ctx,
        JobDbContext db,
        IConfiguration config,
        CancellationToken ct)
    {
        if (!InternalCallerAuth.IsAuthCallerAuthorized(ctx, config))
            return InternalCallerAuth.Unauthorized();

        var domain = WorkEmailDomain.Normalize(request.Domain);
        if (domain is null || WorkEmailDomain.IsBlocked(domain, CompanySignUpSettings.ReadExtraBlocklist(config)))
            return Results.Ok(new DomainMatchResponse(null));

        var companyId = await db.Companies.AsNoTracking()
            .Where(c => c.Verified && c.EmailDomain == domain)
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync(ct);
        return Results.Ok(new DomainMatchResponse(companyId));
    }

    /// <summary>
    /// POST /api/internal/companies: creates the Company for a founder who verified a work email at a domain
    /// no verified Company holds (AUTH-01-10, AUTH-01-11). Idempotent for the same founder, name, tax code and
    /// domain, so an auth-svc retry after a lost response does not create a second Company.
    /// </summary>
    public static async Task<IResult> CreateCompanyForFounderAsync(
        CreateCompanyRequest request,
        HttpContext ctx,
        JobDbContext db,
        IConfiguration config,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (!InternalCallerAuth.IsAuthCallerAuthorized(ctx, config))
            return InternalCallerAuth.Unauthorized();

        var name = request.Name?.Trim();
        var taxCode = string.IsNullOrWhiteSpace(request.TaxCode) ? null : request.TaxCode.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > Company.NameMaxLength ||
            (taxCode is not null && taxCode.Length > Company.TaxCodeMaxLength) ||
            request.CreatedBy == Guid.Empty)
        {
            return ApiErrors.Error(422, CompanyInvalidError, "name and createdBy are required; name and taxCode must be within the length limits.");
        }

        var domain = WorkEmailDomain.Normalize(request.EmailDomain);
        if (domain is null || WorkEmailDomain.IsBlocked(domain, CompanySignUpSettings.ReadExtraBlocklist(config)))
            return ApiErrors.Error(422, DomainBlockedError, "A work email domain is required.");

        var existing = await db.Companies.AsNoTracking()
            .FirstOrDefaultAsync(c => c.CreatedBy == request.CreatedBy, ct);
        if (existing is not null)
        {
            var sameFounderCompany = existing.EmailDomain == domain
                                     && existing.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                                     && string.Equals(existing.TaxCode, taxCode, StringComparison.OrdinalIgnoreCase);
            return sameFounderCompany
                ? Results.Ok(new CreateCompanyResponse(existing.Id, domain, existing.Verified))
                : ApiErrors.Error(409, AccountAlreadyHasCompanyError, "This account already belongs to a company.");
        }

        var conflict = await CompanyUniqueness.FindConflictAsync(db, name, taxCode, domain, ct);
        if (conflict != CompanyConflict.None)
            return ConflictResult(conflict);

        var company = new Company(
            name,
            request.CreatedBy,
            taxCode,
            emailDomain: domain,
            verified: true);
        db.Companies.Add(company);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (CompanyUniqueness.FromException(ex) is { } raced && raced != CompanyConflict.None)
        {
            // Race: a concurrent insert took the name, tax code or domain after the lookup above.
            return ConflictResult(raced);
        }

        loggerFactory.CreateLogger("InternalCompanyEndpoints").LogInformation("Company {CompanyId} created for a verified founder at domain {Domain}.", company.Id, domain);
        return Results.Created($"/api/companies/{company.Id}",
            new CreateCompanyResponse(company.Id, domain, company.Verified));
    }

    /// <summary>
    /// POST /api/internal/company-join-requests: a same-domain sign-up asks to join the verified Company
    /// (AUTH-01-12). Idempotent for the same account and Company; one request per account.
    /// </summary>
    public static async Task<IResult> CreateJoinRequestAsync(
        CreateJoinRequestRequest request,
        HttpContext ctx,
        JobDbContext db,
        IConfiguration config,
        CancellationToken ct)
    {
        if (!InternalCallerAuth.IsAuthCallerAuthorized(ctx, config))
            return InternalCallerAuth.Unauthorized();
        if (request.UserId == Guid.Empty || request.CompanyId == Guid.Empty)
            return ApiErrors.Error(422, CompanyInvalidError, "userId and companyId are required.");

        var company = await db.Companies.AsNoTracking()
            .Where(c => c.Id == request.CompanyId)
            .Select(c => new { c.Verified })
            .FirstOrDefaultAsync(ct);
        if (company is null)
            return ApiErrors.Error(404, "COMPANY_NOT_FOUND", "Company not found.");
        if (!company.Verified)
            return ApiErrors.Error(422, CompanyNotVerifiedError, "Join requests are only accepted for a verified company.");

        var existing = await db.CompanyJoinRequests.AsNoTracking()
            .FirstOrDefaultAsync(r => r.UserId == request.UserId, ct);
        if (existing is not null)
            return ExistingJoinRequest(existing, request.CompanyId);

        var entity = new CompanyJoinRequest(
            request.CompanyId,
            request.UserId,
            DateTimeOffset.UtcNow,
            CompanySignUpSettings.ReadJoinRequestWaitingPeriod(config));
        db.CompanyJoinRequests.Add(entity);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: CompanyUniqueness.UniqueViolationState })
        {
            // Concurrent create for the same account: the unique UserId index won. Answer from the winner.
            var winner = await db.CompanyJoinRequests.AsNoTracking()
                .FirstOrDefaultAsync(r => r.UserId == request.UserId, ct);
            if (winner is null)
                throw;
            return ExistingJoinRequest(winner, request.CompanyId);
        }

        return Results.Created($"/api/companies/me/join-request",
            new JoinRequestResponse(entity.Id, entity.CompanyId, entity.UserId, entity.Status.ToString(), entity.ExpiresAt));
    }

    /// <summary>
    /// POST /api/internal/company-join-requests/{id}/support-decision: platform support decides an escalated request
    /// (AUTH-01-14). Approve links the account through auth-svc first, then makes it the Company admin and revokes the
    /// previous admin's unused invite codes. The request stays Escalated when auth-svc is unavailable.
    /// </summary>
    public static async Task<IResult> SupportDecisionAsync(
        Guid id,
        SupportDecisionRequest request,
        HttpContext ctx,
        JobDbContext db,
        IConfiguration config,
        IAuthCompanyLinker linker,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (!InternalCallerAuth.IsAuthCallerAuthorized(ctx, config))
            return InternalCallerAuth.Unauthorized();

        var decision = request.Decision?.Trim().ToLowerInvariant();
        var approve = decision == "approve";
        var reason = request.Reason?.Trim();
        var operatorName = request.Operator?.Trim();
        if (!approve && decision != "reject")
            return ApiErrors.Error(422, SupportDecisionInvalidError, "decision must be approve or reject.");
        if (string.IsNullOrEmpty(operatorName) || operatorName.Length > 128)
            return ApiErrors.Error(422, SupportDecisionInvalidError, "operator is required (max 128 characters).");

        var joinRequest = await db.CompanyJoinRequests.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (joinRequest is null)
            return ApiErrors.Error(404, JoinRequestNotFoundError, "Join request not found.");
        if (joinRequest.Status != CompanyJoinRequestStatus.Escalated)
            return ApiErrors.Error(409, JoinRequestStateInvalidError, "Only an escalated join request can be decided.");

        var now = DateTimeOffset.UtcNow;
        if (approve)
        {
            var company = await db.Companies.FirstOrDefaultAsync(c => c.Id == joinRequest.CompanyId, ct);
            if (company is null)
                return ApiErrors.Error(404, "COMPANY_NOT_FOUND", "Company not found.");

            var link = await linker.LinkCompanyAsync(joinRequest.UserId, company.Id, ct);
            if (link == LinkCompanyResult.AlreadyHasCompany)
                return ApiErrors.Error(409, AccountAlreadyHasCompanyError, "The requester already belongs to another company.");
            if (link == LinkCompanyResult.Unavailable)
                return ApiErrors.Error(503, LinkUnavailableError, "The account service could not link the company. Retry later.");

            var previousAdmin = company.CreatedBy;
            if (previousAdmin != joinRequest.UserId)
            {
                company.TransferAdmin(joinRequest.UserId);
                var unused = await db.InviteCodes
                    .Where(c => c.CompanyId == company.Id && c.CreatedBy == previousAdmin)
                    .ToListAsync(ct);
                foreach (var code in unused)
                    code.RevokeUnused(now, InviteCode.RevokeReasonAdminChanged);
            }
            joinRequest.DecideBySupport(approve: true, reason, now);
        }
        else
        {
            joinRequest.DecideBySupport(approve: false, reason, now);
        }

        await db.SaveChangesAsync(ct);
        loggerFactory.CreateLogger("InternalCompanyEndpoints").LogInformation(
            "Support decision {Decision} on join request {JoinRequestId} (company {CompanyId}, user {UserId}), reason {Reason}, operator {Operator}, at {DecidedAt:O}.",
            approve ? "approve" : "reject", joinRequest.Id, joinRequest.CompanyId, joinRequest.UserId,
            reason ?? "", operatorName, now);

        return Results.Ok(new JoinRequestResponse(
            joinRequest.Id, joinRequest.CompanyId, joinRequest.UserId, joinRequest.Status.ToString(), joinRequest.ExpiresAt));
    }

    private static IResult ConflictResult(CompanyConflict conflict) => conflict switch
    {
        CompanyConflict.Domain => ApiErrors.Error(409, DomainTakenError, "The work email domain already belongs to a company."),
        CompanyConflict.TaxCode => ApiErrors.Error(409, TaxCodeTakenError, "The tax code is already registered."),
        _ => ApiErrors.Error(409, NameTakenError, "The company name is already registered."),
    };

    private static IResult ExistingJoinRequest(CompanyJoinRequest existing, Guid companyId) =>
        existing.CompanyId == companyId
            ? Results.Ok(new JoinRequestResponse(existing.Id, existing.CompanyId, existing.UserId, existing.Status.ToString(), existing.ExpiresAt))
            : ApiErrors.Error(409, JoinRequestExistsError, "This account already has a join request.");
}
