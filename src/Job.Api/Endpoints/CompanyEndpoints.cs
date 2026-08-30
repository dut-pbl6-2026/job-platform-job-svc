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
/// PUT    /api/companies/{id} — Update company (Recruiter)
/// </summary>
public static class CompanyEndpoints
{
    public static WebApplication MapCompanyEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/companies").WithTags("Companies");

        group.MapPost("/", CreateCompany);
        group.MapGet("/", GetCompanies);
        group.MapGet("/{id:guid}", GetCompanyById);
        group.MapPut("/{id:guid}", UpdateCompany);

        return app;
    }

    // Helpers: avoid Results.Forbid()/Unauthorized() which require a registered auth handler.
    // In Development, DevAuthMiddleware is registered instead of JWT Bearer, so Forbid() would
    // throw InvalidOperationException. Use explicit status codes instead.
    private static IResult UnauthorizedResult() =>
        Results.Json(new { message = "Unauthorized. Missing or invalid X-User-Id." }, statusCode: 401);

    private static IResult ForbiddenResult(string message = "Forbidden. Recruiter role required.") =>
        Results.Json(new { message }, statusCode: 403);

    /// <summary>
    /// POST /api/companies — Recruiter creates a new company profile.
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

        if (string.IsNullOrWhiteSpace(dto.Name))
            return Results.ValidationProblem(new Dictionary<string, string[]>
            { ["name"] = ["Company name is required."] });

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
            company = new Company(trimmedName, dto.TaxCode);
            // Populate optional fields via Update() to keep entity clean
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
    /// Supports keyword search on Name, Industry, Address.
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

        var query = db.Companies.AsQueryable();
        var searchTerm = (q ?? search)?.Trim().ToLower();

        if (!string.IsNullOrEmpty(searchTerm))
        {
            query = query.Where(c =>
                c.Name.ToLower().Contains(searchTerm) ||
                (c.Industry != null && c.Industry.ToLower().Contains(searchTerm)) ||
                (c.Address != null && c.Address.ToLower().Contains(searchTerm))
            );
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
        var company = await db.Companies.FirstOrDefaultAsync(c => c.Id == id);
        if (company is null)
            return Results.NotFound(new { message = "Company not found" });

        return Results.Ok(CompanyDetailDto.From(company));
    }

    /// <summary>
    /// PUT /api/companies/{id} — Recruiter updates company.
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

        if (string.IsNullOrWhiteSpace(dto.Name))
            return Results.ValidationProblem(new Dictionary<string, string[]>
            { ["name"] = ["Company name is required."] });

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

    private static (string? userId, string? role) GetIdentity(HttpContext ctx)
    {
        var userId = ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var role = ctx.User.FindFirst(ClaimTypes.Role)?.Value;
        return (userId, role);
    }
}
