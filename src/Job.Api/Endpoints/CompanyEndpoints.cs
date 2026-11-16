using Job.Api.Auth;
using Job.Api.DTOs;
using Job.Core.Entities;
using Job.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Job.Api.Endpoints;

/// <summary>
/// Company endpoints per SRS D.1 lines 220-223 and JOB-01.
/// POST   /api/companies      — always 409 ACCOUNT_ALREADY_HAS_COMPANY for a Recruiter (AUTH-01-06)
/// GET    /api/companies      — Public list with search &amp; pagination
/// GET    /api/companies/mine — Recruiter's own company plus isCompanyAdmin
/// GET    /api/companies/{id} — Public detail
/// GET    /api/companies/{id}/jobs — Public Active jobs of that company (company page), 0-based paging
/// PUT    /api/companies/{id} — Update company (owner Recruiter only)
/// NOTE: no DELETE /api/companies/{id} by design — SRS D.1 defines POST/GET/GET/PUT
/// only for companies, and Jobs.CompanyId FK uses Restrict (a referenced company
/// cannot be removed). If deletion is ever required, add soft-delete first.
/// </summary>
public static class CompanyEndpoints
{
    public const string AccountAlreadyHasCompanyError = "ACCOUNT_ALREADY_HAS_COMPANY";

    public static WebApplication MapCompanyEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/companies").WithTags("Companies");

        group.MapPost("/", CreateCompany)
            .WithName("CreateCompany")
            .WithSummary("Always 409 for a Recruiter: a Recruiter account already has its Company (AUTH-01-06)")
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict);

        group.MapGet("/", GetCompanies)
            .WithName("GetCompanies")
            .WithSummary("List companies with search and pagination (Public)")
            .Produces<PaginatedResponse<CompanyDetailDto>>(StatusCodes.Status200OK);

        group.MapGet("/{id:guid}", GetCompanyById)
            .WithName("GetCompanyById")
            .WithSummary("Get company detail by ID (Public)")
            .Produces<CompanyDetailDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/mine", GetMyCompany)
            .WithName("GetMyCompany")
            .WithSummary("Get the signed-in recruiter's company and whether they are its admin")
            .Produces<CompanyMineDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/{id:guid}/jobs", GetCompanyJobs)
            .WithName("GetCompanyJobs")
            .WithSummary("List a company's Active jobs, newest first (Public company page)")
            .Produces<PaginatedResponse<PublicJobSummaryDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPut("/{id:guid}", UpdateCompany)
            .WithName("UpdateCompany")
            .WithSummary("Update company profile (Owner Recruiter only)")
            .Produces(StatusCodes.Status200OK)
            .ProducesValidationProblem(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        return app;
    }

    // Helpers: avoid Results.Forbid()/Unauthorized() which require a registered auth handler.
    // In Development, DevAuthMiddleware is used instead of JWT Bearer, so Forbid() would
    // throw InvalidOperationException. Use explicit status codes instead.
    private static IResult UnauthorizedResult() =>
        Results.Json(new { message = "Unauthorized. Missing or invalid user identity." }, statusCode: 401);

    private static IResult ForbiddenResult(string message = "Forbidden. Recruiter role required.") =>
        Results.Json(new { message }, statusCode: 403);

    /// <summary>
    /// Validates company fields (required + column length) so oversized input returns
    /// 400 ValidationProblem instead of an unhandled Npgsql 22001 → 500. Lengths mirror
    /// Company domain consts / JobDbContext fluent config.
    /// </summary>
    private static IResult? ValidateCompanyFields(
        string name,
        string? taxCode,
        string? logoUrl,
        string? website,
        string? address,
        string? industry,
        string? size)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(name))
        {
            errors["name"] = ["Company name is required."];
        }
        else if (name.Trim().Length > Company.NameMaxLength)
        {
            errors["name"] = [$"Company name must not exceed {Company.NameMaxLength} characters."];
        }

        CheckLength(errors, "taxCode", taxCode, Company.TaxCodeMaxLength);
        CheckLength(errors, "logoUrl", logoUrl, Company.LogoUrlMaxLength);
        CheckLength(errors, "website", website, Company.WebsiteMaxLength);
        CheckLength(errors, "address", address, Company.AddressMaxLength);
        CheckLength(errors, "industry", industry, Company.IndustryMaxLength);
        CheckLength(errors, "size", size, Company.SizeMaxLength);

        return errors.Count == 0 ? null : Results.ValidationProblem(errors);
    }

    private static void CheckLength(Dictionary<string, string[]> errors, string field, string? value, int max)
    {
        if (!string.IsNullOrWhiteSpace(value) && value.Trim().Length > max)
            errors[field] = [$"{field} must not exceed {max} characters."];
    }

    /// <summary>
    /// POST /api/companies (SRS AUTH-01-06). A Recruiter account already has exactly one Company, set at
    /// company sign-up or by invite code, so this endpoint always answers
    /// <c>409 ACCOUNT_ALREADY_HAS_COMPANY</c> for a Recruiter and creates nothing (backlog C14 for removal).
    /// The gateway JWT carries no company claim, so job-svc cannot tell a Recruiter apart by company.
    /// Other callers keep the role rule (403).
    /// </summary>
    public static IResult CreateCompany(HttpContext ctx)
    {
        var (userId, role) = IdentityHelper.GetIdentity(ctx);
        if (userId is null)
            return UnauthorizedResult();
        if (role != "Recruiter")
            return ForbiddenResult();

        return ApiErrors.Error(409, AccountAlreadyHasCompanyError,
            "This account already belongs to a company.");
    }

    /// <summary>
    /// GET /api/companies?q=...&amp;page=1&amp;size=10 — public, no auth required.
    /// Case-insensitive search over Name, Description, Industry and Address via
    /// EF.Functions.ILike (Postgres, parameterized, wildcard-escaped). Providers that
    /// cannot translate ILike (e.g. InMemory unit tests) fall back to
    /// .ToLower().Contains(). Returns paginated list of CompanyDetailDto.
    /// TECH DEBT (JOB-01): the try-ILike-then-fallback keeps provider detection out of
    /// prod code; extract an ISearchStrategy if more providers ever appear.
    /// </summary>
    private static async Task<IResult> GetCompanies(
        JobDbContext db,
        string? q = null,
        string? search = null,
        int page = 1,
        int size = 10)
    {
        size = Math.Clamp(size, 1, 100);
        page = Math.Max(1, page);

        var searchTerm = (q ?? search)?.Trim();

        int total;
        List<Company> items;
        try
        {
            var query = ApplySearch(db.Companies.AsNoTracking(), searchTerm, useILike: true);
            total = await query.CountAsync();
            items = await query
                .OrderBy(c => c.Name)
                .Skip((page - 1) * size)
                .Take(size)
                .ToListAsync();
        }
        catch (InvalidOperationException)
        {
            // Provider cannot translate EF.Functions.ILike (e.g. InMemory unit tests) —
            // retry with case-insensitive Contains. Never triggered on Postgres:
            // infra failures surface as NpgsqlException, not InvalidOperationException.
            var fallback = ApplySearch(db.Companies.AsNoTracking(), searchTerm, useILike: false);
            total = await fallback.CountAsync();
            items = await fallback
                .OrderBy(c => c.Name)
                .Skip((page - 1) * size)
                .Take(size)
                .ToListAsync();
        }

        var dtos = items.Select(CompanyDetailDto.From).ToList();
        var totalPages = (int)Math.Ceiling((double)total / size);

        return Results.Ok(new PaginatedResponse<CompanyDetailDto>(dtos, total, page, size, totalPages));
    }

    private static IQueryable<Company> ApplySearch(IQueryable<Company> query, string? searchTerm, bool useILike)
    {
        if (string.IsNullOrEmpty(searchTerm))
            return query;

        if (useILike)
        {
            // Postgres — ILike uses pg_trgm/ilike index-friendly operator (SEC-05: parameterized).
            // Escape % _ \ so user input behaves literally (no accidental wildcard injection).
            var pattern = $"%{EscapeLike(searchTerm)}%";
            return query.Where(c =>
                EF.Functions.ILike(c.Name, pattern, "\\") ||
                (c.Description != null && EF.Functions.ILike(c.Description, pattern, "\\")) ||
                (c.Industry != null && EF.Functions.ILike(c.Industry, pattern, "\\")) ||
                (c.Address != null && EF.Functions.ILike(c.Address, pattern, "\\")));
        }

        // Providers without ILike translation (e.g. InMemory unit tests).
        var lower = searchTerm.ToLower();
        return query.Where(c =>
            c.Name.ToLower().Contains(lower) ||
            (c.Description != null && c.Description.ToLower().Contains(lower)) ||
            (c.Industry != null && c.Industry.ToLower().Contains(lower)) ||
            (c.Address != null && c.Address.ToLower().Contains(lower)));
    }

    private static string EscapeLike(string term) =>
        term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>
    /// GET /api/companies/{id}/jobs?page=0&amp;size=20 — public company page listing.
    /// Active jobs only (soft-deleted and Closed are hidden), newest first, 0-based paging like search
    /// (size capped at 100). Returns 404 for an unknown company so a broken link is not an empty page.
    /// </summary>
    private static async Task<IResult> GetCompanyJobs(
        Guid id,
        JobDbContext db,
        int page = 0,
        int size = 20,
        CancellationToken ct = default)
    {
        page = Math.Max(0, page);
        size = Math.Clamp(size, 1, 100);

        var companyExists = await db.Companies.AsNoTracking().AnyAsync(c => c.Id == id, ct);
        if (!companyExists)
            return Results.NotFound(new { message = "Company not found" });

        var query = db.Jobs.AsNoTracking()
            .Where(j => j.CompanyId == id && j.Status == JobStatus.Active);

        var total = await query.CountAsync(ct);
        var items = await query
            .Include(j => j.Company)
            .Include(j => j.Category)
            .OrderByDescending(j => j.CreatedAt)
            .ThenByDescending(j => j.Id)
            .Skip(page * size)
            .Take(size)
            .ToListAsync(ct);

        var dtos = items.Select(PublicJobSummaryDto.From).ToList();
        var totalPages = (int)Math.Ceiling((double)total / size);
        return Results.Ok(new PaginatedResponse<PublicJobSummaryDto>(dtos, total, page, size, totalPages));
    }

    /// <summary>
    /// GET /api/companies/mine — the company linked to the signed-in Recruiter, resolved the same way
    /// as POST /api/jobs: the <c>company_id</c> claim, falling back to <c>CreatedBy</c> for older tokens.
    /// <c>isCompanyAdmin</c> is true only for the company creator (<c>CreatedBy == userId</c>); other
    /// recruiters of the company are members with view-only access. 404 when no company is linked.
    /// </summary>
    private static async Task<IResult> GetMyCompany(
        JobDbContext db,
        HttpContext ctx,
        CancellationToken ct = default)
    {
        var (userId, role) = IdentityHelper.GetIdentity(ctx);
        if (userId is null)
            return UnauthorizedResult();
        if (role != "Recruiter")
            return ForbiddenResult();

        var claimedCompanyId = IdentityHelper.GetCompanyId(ctx);
        Company? company = claimedCompanyId is { } companyId
            ? await db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == companyId, ct)
            : null;

        // Older tokens carry no company claim: the creator's own company is the fallback.
        company ??= await db.Companies.AsNoTracking()
            .FirstOrDefaultAsync(c => c.CreatedBy == userId.Value, ct);

        if (company is null)
            return Results.NotFound(new { message = "No company is linked to this account." });

        return Results.Ok(CompanyMineDto.From(company, userId.Value));
    }

    /// <summary>
    /// GET /api/companies/{id} — public, returns 404 if not found.
    /// </summary>
    private static async Task<IResult> GetCompanyById(Guid id, JobDbContext db)
    {
        var company = await db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        if (company is null)
            return Results.NotFound(new { message = "Company not found" });

        return Results.Ok(CompanyDetailDto.From(company));
    }

    /// <summary>
    /// PUT /api/companies/{id} — Recruiter updates company they own (CreatedBy == userId).
    /// Returns 403 if the authenticated recruiter did not create this company.
    /// Validates uniqueness of Name and TaxCode against other companies.
    /// Returns 200 OK.
    /// </summary>
    private static async Task<IResult> UpdateCompany(
        Guid id,
        CompanyUpdateDto dto,
        JobDbContext db,
        HttpContext ctx)
    {
        var (userId, role) = IdentityHelper.GetIdentity(ctx);
        if (userId is null)
            return UnauthorizedResult();
        if (role != "Recruiter")
            return ForbiddenResult();

        var company = await db.Companies.FirstOrDefaultAsync(c => c.Id == id);
        if (company is null)
            return Results.NotFound(new { message = "Company not found" });

        // Ownership check — only the recruiter who created the company can update it
        if (company.CreatedBy != userId.Value)
            return ForbiddenResult("Forbidden. You do not own this company.");

        var fieldErrors = ValidateCompanyFields(
            dto.Name, dto.TaxCode, dto.LogoUrl, dto.Website,
            dto.Address, dto.Industry, dto.Size);
        if (fieldErrors is not null)
            return fieldErrors;

        var trimmedName = dto.Name.Trim();

        var nameConflict = await db.Companies
            .AnyAsync(c => c.Id != id && c.Name.ToLower() == trimmedName.ToLower());
        if (nameConflict)
            return Results.Conflict(new { message = "Company name is already taken by another company." });

        if (!string.IsNullOrWhiteSpace(dto.TaxCode))
        {
            // MST codes are numeric — compare case-insensitively for robustness
            var trimmedTax = dto.TaxCode.Trim().ToLower();
            var taxConflict = await db.Companies
                .AnyAsync(c => c.Id != id && c.TaxCode != null && c.TaxCode.ToLower() == trimmedTax);
            if (taxConflict)
                return Results.Conflict(new { message = "Tax code is already taken by another company." });
        }

        try
        {
            company.Update(
                trimmedName,
                dto.TaxCode,
                dto.LogoUrl,
                dto.Website,
                dto.Description,
                dto.Address,
                dto.Industry,
                dto.Size
            );
        }
        catch (ArgumentException ex)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            { ["request"] = [ex.Message] });
        }

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            // Concurrent duplicate slipped past the AnyAsync check — return 409 instead of 500.
            return Results.Conflict(new { message = "Company name or tax code already exists." });
        }
        return Results.Ok(new { message = "Company updated successfully" });
    }
}
