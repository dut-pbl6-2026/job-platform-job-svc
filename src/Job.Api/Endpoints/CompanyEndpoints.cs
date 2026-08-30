using System.Security.Claims;
using Job.Api.DTOs;
using Job.Core.Entities;
using Job.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Job.Api.Endpoints;

/// <summary>
/// Company CRUD endpoints per SRS D.1 line 220-222 and JOB-01.
/// POST   /api/companies      — Create company (Recruiter only)
/// GET    /api/companies      — Public list with search &amp; pagination
/// GET    /api/companies/{id} — Public detail
/// PUT    /api/companies/{id} — Update company (owner Recruiter only)
/// </summary>
public static class CompanyEndpoints
{
    public static WebApplication MapCompanyEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/companies").WithTags("Companies");

        group.MapPost("/", CreateCompany)
            .WithName("CreateCompany")
            .WithSummary("Create a new company profile (Recruiter only)")
            .Produces(StatusCodes.Status201Created)
            .ProducesValidationProblem(StatusCodes.Status400BadRequest)
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
    /// POST /api/companies — Recruiter creates a new company profile.
    /// Sets CreatedBy to the current recruiter's userId.
    /// Validates uniqueness of Name and TaxCode (SEC-05 EF parameterized queries).
    /// Returns 201 Created with {id, message}.
    /// </summary>
    private static async Task<IResult> CreateCompany(
        CompanyCreateDto dto,
        JobDbContext db,
        HttpContext ctx)
    {
        var (userId, role) = GetIdentity(ctx);
        if (userId is null)
            return UnauthorizedResult();
        if (role != "Recruiter")
            return ForbiddenResult();

        var fieldErrors = ValidateCompanyFields(
            dto.Name, dto.TaxCode, dto.LogoUrl, dto.Website,
            dto.Address, dto.Industry, dto.Size);
        if (fieldErrors is not null)
            return fieldErrors;

        var trimmedName = dto.Name.Trim();

        // SEC-05: parameterized via EF — no raw string concat
        var nameExists = await db.Companies
            .AnyAsync(c => c.Name.ToLower() == trimmedName.ToLower());
        if (nameExists)
            return Results.Conflict(new { message = "Company name already exists." });

        if (!string.IsNullOrWhiteSpace(dto.TaxCode))
        {
            var trimmedTax = dto.TaxCode.Trim();
            var taxExists = await db.Companies.AnyAsync(c => c.TaxCode == trimmedTax);
            if (taxExists)
                return Results.Conflict(new { message = "Tax code already registered." });
        }

        Company company;
        try
        {
            // CreatedBy = current recruiter's userId (ownership)
            company = new Company(
                trimmedName,
                userId.Value,
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

        db.Companies.Add(company);
        await db.SaveChangesAsync();

        return Results.Created($"/api/companies/{company.Id}", new
        {
            id = company.Id,
            message = "Company created successfully"
        });
    }

    /// <summary>
    /// GET /api/companies?q=...&amp;page=1&amp;size=10 — public, no auth required.
    /// Uses EF.Functions.ILike (Postgres) for case-insensitive search; falls back to
    /// .ToLower().Contains() on InMemory (unit tests) which does not support ILike.
    /// Returns paginated list of CompanyDetailDto.
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

        var query = db.Companies.AsNoTracking();
        var searchTerm = (q ?? search)?.Trim();

        if (!string.IsNullOrEmpty(searchTerm))
        {
            var isInMemory = db.Database.ProviderName ==
                "Microsoft.EntityFrameworkCore.InMemory";

            if (isInMemory)
            {
                // InMemory (unit tests) — EF.Functions.ILike not supported
                var lower = searchTerm.ToLower();
                query = query.Where(c =>
                    c.Name.ToLower().Contains(lower) ||
                    (c.Industry != null && c.Industry.ToLower().Contains(lower)) ||
                    (c.Address != null && c.Address.ToLower().Contains(lower))
                );
            }
            else
            {
                // Postgres — ILike uses pg_ilike index-friendly operator (SEC-05: parameterized).
                // Escape % _ \ so user input behaves literally (no accidental wildcard injection).
                var escaped = searchTerm
                    .Replace("\\", "\\\\")
                    .Replace("%", "\\%")
                    .Replace("_", "\\_");
                var pattern = $"%{escaped}%";
                query = query.Where(c =>
                    EF.Functions.ILike(c.Name, pattern, "\\") ||
                    (c.Industry != null && EF.Functions.ILike(c.Industry, pattern, "\\")) ||
                    (c.Address != null && EF.Functions.ILike(c.Address, pattern, "\\"))
                );
            }
        }

        var total = await query.CountAsync();
        var items = await query
            .OrderBy(c => c.Name)
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync();

        var dtos = items.Select(CompanyDetailDto.From).ToList();
        var totalPages = (int)Math.Ceiling((double)total / size);

        return Results.Ok(new PaginatedResponse<CompanyDetailDto>(dtos, total, page, size, totalPages));
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
        var (userId, role) = GetIdentity(ctx);
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
            var trimmedTax = dto.TaxCode.Trim();
            var taxConflict = await db.Companies
                .AnyAsync(c => c.Id != id && c.TaxCode == trimmedTax);
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

        await db.SaveChangesAsync();
        return Results.Ok(new { message = "Company updated successfully" });
    }

    private static (Guid? userId, string? role) GetIdentity(HttpContext ctx)
    {
        var rawUserId = ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var role = ctx.User.FindFirst(ClaimTypes.Role)?.Value;
        if (rawUserId is null || !Guid.TryParse(rawUserId, out var userId))
            return (null, role);
        return (userId, role);
    }
}
