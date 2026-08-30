using System.Security.Claims;
using Job.Api.DTOs;
using Job.Api.Endpoints;
using Job.Core.Entities;
using Job.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Job.Tests;

/// <summary>
/// Unit tests for Company API endpoints using In-Memory EF (no real DB needed).
/// Tests per JOB_COMPANY_API_PLAN.md section 1.4 (updated with ownership checks).
/// </summary>
public class CompanyEndpointsTests : IDisposable
{
    private readonly JobDbContext _db;
    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly Guid _otherId = Guid.NewGuid();

    public CompanyEndpointsTests()
    {
        var options = new DbContextOptionsBuilder<JobDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new JobDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose() => _db.Dispose();

    // ─── Helpers ────────────────────────────────────────────────────────────

    private static HttpContext BuildContext(Guid userId, string role = "Recruiter")
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Role, role)
        };
        return new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
        };
    }

    private static HttpContext BuildAnonymousContext() =>
        new DefaultHttpContext { User = new ClaimsPrincipal() };

    // ─── POST /api/companies ─────────────────────────────────────────────────

    [Fact]
    public async Task CreateCompany_Success_Returns201_AndSetsCreatedBy()
    {
        var dto = new CompanyCreateDto("TechCorp VN", "0123456789", Industry: "IT");
        var ctx = BuildContext(_ownerId);

        var result = await CompanyEndpointsInvoker.CreateCompany(dto, _db, ctx);

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(201, status.StatusCode);

        var saved = await _db.Companies.FirstOrDefaultAsync(c => c.Name == "TechCorp VN");
        Assert.NotNull(saved);
        Assert.Equal("IT", saved!.Industry);
        Assert.Equal("0123456789", saved.TaxCode);
        // Ownership field must be set to the recruiter who created it
        Assert.Equal(_ownerId, saved.CreatedBy);
    }

    [Fact]
    public async Task CreateCompany_MissingName_Returns400()
    {
        var dto = new CompanyCreateDto("");
        var ctx = BuildContext(_ownerId);

        var result = await CompanyEndpointsInvoker.CreateCompany(dto, _db, ctx);

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(400, status.StatusCode);
    }

    [Fact]
    public async Task CreateCompany_DuplicateName_Returns409()
    {
        _db.Companies.Add(new Company("DuplicateCorp", _ownerId));
        await _db.SaveChangesAsync();

        var dto = new CompanyCreateDto("DuplicateCorp");
        var ctx = BuildContext(_ownerId);

        var result = await CompanyEndpointsInvoker.CreateCompany(dto, _db, ctx);

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(409, status.StatusCode);
    }

    [Fact]
    public async Task CreateCompany_DuplicateTaxCode_Returns409()
    {
        _db.Companies.Add(new Company("ExistingCorp", _ownerId, "TAX-001"));
        await _db.SaveChangesAsync();

        var dto = new CompanyCreateDto("NewCorp", TaxCode: "TAX-001");
        var ctx = BuildContext(_ownerId);

        var result = await CompanyEndpointsInvoker.CreateCompany(dto, _db, ctx);

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(409, status.StatusCode);
    }

    [Fact]
    public async Task CreateCompany_Unauthenticated_Returns401()
    {
        var dto = new CompanyCreateDto("SomeCorp");
        var ctx = BuildAnonymousContext();

        var result = await CompanyEndpointsInvoker.CreateCompany(dto, _db, ctx);

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(401, status.StatusCode);
    }

    [Fact]
    public async Task CreateCompany_InvalidGuidUserId_Returns401()
    {
        var dto = new CompanyCreateDto("SomeCorp");
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "invalid-guid"),
            new(ClaimTypes.Role, "Recruiter")
        };
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
        };

        var result = await CompanyEndpointsInvoker.CreateCompany(dto, _db, ctx);

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(401, status.StatusCode);
    }

    [Fact]
    public async Task CreateCompany_NonRecruiter_Returns403()
    {
        var dto = new CompanyCreateDto("SomeCorp");
        var ctx = BuildContext(_ownerId, role: "User");

        var result = await CompanyEndpointsInvoker.CreateCompany(dto, _db, ctx);

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(403, status.StatusCode);
    }

    // ─── GET /api/companies ─────────────────────────────────────────────────

    [Fact]
    public async Task GetCompanies_NoPagination_ReturnsAll()
    {
        _db.Companies.AddRange(
            new Company("Alpha Corp", _ownerId),
            new Company("Beta Ltd", _ownerId),
            new Company("Gamma Inc", _ownerId)
        );
        await _db.SaveChangesAsync();

        var result = await CompanyEndpointsInvoker.GetCompanies(_db, q: null, search: null, page: 1, size: 10);

        var ok = Assert.IsType<Ok<PaginatedResponse<CompanyDetailDto>>>(result);
        Assert.Equal(3, ok.Value!.Total);
        Assert.Equal(3, ok.Value.Items.Count);
    }

    [Fact]
    public async Task GetCompanies_SearchByName_FiltersCorrectly()
    {
        _db.Companies.AddRange(
            new Company("TechViet", _ownerId),
            new Company("Finance Plus", _ownerId),
            new Company("Tech Solutions", _ownerId)
        );
        await _db.SaveChangesAsync();

        // InMemory fallback: ToLower().Contains() — ILike is Postgres-only
        var result = await CompanyEndpointsInvoker.GetCompanies(_db, q: "tech", search: null, page: 1, size: 10);

        var ok = Assert.IsType<Ok<PaginatedResponse<CompanyDetailDto>>>(result);
        Assert.Equal(2, ok.Value!.Total);
        Assert.All(ok.Value.Items, item =>
            Assert.Contains("tech", item.Name.ToLower()));
    }

    [Fact]
    public async Task GetCompanies_Pagination_ReturnsCorrectPage()
    {
        for (int i = 1; i <= 15; i++)
            _db.Companies.Add(new Company($"Company {i:D2}", _ownerId));
        await _db.SaveChangesAsync();

        var result = await CompanyEndpointsInvoker.GetCompanies(_db, q: null, search: null, page: 2, size: 5);

        var ok = Assert.IsType<Ok<PaginatedResponse<CompanyDetailDto>>>(result);
        Assert.Equal(15, ok.Value!.Total);
        Assert.Equal(5, ok.Value.Items.Count);
        Assert.Equal(2, ok.Value.Page);
        Assert.Equal(3, ok.Value.TotalPages);
    }

    // ─── GET /api/companies/{id} ─────────────────────────────────────────────

    [Fact]
    public async Task GetCompanyById_ExistingId_Returns200()
    {
        var company = new Company("DetailCorp", _ownerId, "TAX-999");
        _db.Companies.Add(company);
        await _db.SaveChangesAsync();

        var result = await CompanyEndpointsInvoker.GetCompanyById(company.Id, _db);

        var ok = Assert.IsType<Ok<CompanyDetailDto>>(result);
        Assert.Equal(company.Id, ok.Value!.Id);
        Assert.Equal("DetailCorp", ok.Value.Name);
        Assert.Equal("TAX-999", ok.Value.TaxCode);
        Assert.Equal(_ownerId, ok.Value.CreatedBy);
    }

    [Fact]
    public async Task GetCompanyById_NotFound_Returns404()
    {
        var result = await CompanyEndpointsInvoker.GetCompanyById(Guid.NewGuid(), _db);

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(404, status.StatusCode);
    }

    // ─── PUT /api/companies/{id} ─────────────────────────────────────────────

    [Fact]
    public async Task UpdateCompany_Owner_Returns200()
    {
        var company = new Company("OldName", _ownerId, "TAX-100");
        _db.Companies.Add(company);
        await _db.SaveChangesAsync();

        var dto = new CompanyUpdateDto("NewName", TaxCode: "TAX-200", Industry: "Finance");
        var ctx = BuildContext(_ownerId); // same as creator

        var result = await CompanyEndpointsInvoker.UpdateCompany(company.Id, dto, _db, ctx);

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(200, status.StatusCode);

        var updated = await _db.Companies.FindAsync(company.Id);
        Assert.Equal("NewName", updated!.Name);
        Assert.Equal("Finance", updated.Industry);
        Assert.Equal(_ownerId, updated.CreatedBy); // ownership unchanged
    }

    [Fact]
    public async Task UpdateCompany_NotOwner_Returns403()
    {
        // Company created by _ownerId, but _otherId tries to update
        var company = new Company("OwnerCorp", _ownerId);
        _db.Companies.Add(company);
        await _db.SaveChangesAsync();

        var dto = new CompanyUpdateDto("HackedName");
        var ctx = BuildContext(_otherId); // different recruiter

        var result = await CompanyEndpointsInvoker.UpdateCompany(company.Id, dto, _db, ctx);

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(403, status.StatusCode);

        // Company should be unchanged
        var unchanged = await _db.Companies.FindAsync(company.Id);
        Assert.Equal("OwnerCorp", unchanged!.Name);
    }

    [Fact]
    public async Task UpdateCompany_NotFound_Returns404()
    {
        var dto = new CompanyUpdateDto("AnyName");
        var ctx = BuildContext(_ownerId);

        var result = await CompanyEndpointsInvoker.UpdateCompany(Guid.NewGuid(), dto, _db, ctx);

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(404, status.StatusCode);
    }

    [Fact]
    public async Task UpdateCompany_DuplicateName_Returns409()
    {
        var company1 = new Company("Corp A", _ownerId);
        var company2 = new Company("Corp B", _ownerId);
        _db.Companies.AddRange(company1, company2);
        await _db.SaveChangesAsync();

        var dto = new CompanyUpdateDto("Corp A"); // conflict with company1
        var ctx = BuildContext(_ownerId); // owns company2

        var result = await CompanyEndpointsInvoker.UpdateCompany(company2.Id, dto, _db, ctx);

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(409, status.StatusCode);
    }

    [Fact]
    public async Task UpdateCompany_Unauthenticated_Returns401()
    {
        var company = new Company("SomeCorp", _ownerId);
        _db.Companies.Add(company);
        await _db.SaveChangesAsync();

        var dto = new CompanyUpdateDto("NewName");
        var ctx = BuildAnonymousContext();

        var result = await CompanyEndpointsInvoker.UpdateCompany(company.Id, dto, _db, ctx);

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(401, status.StatusCode);
    }

    // ─── DTO mapping ─────────────────────────────────────────────────────────

    [Fact]
    public void CompanyDetailDto_From_MapsAllFields()
    {
        var company = new Company("MapCorp", _ownerId, "TAX-MAP");
        company.Update("MapCorp", "TAX-MAP", "https://logo.png", "https://map.co",
            "A description", "123 Street", "Technology", "50-200");

        var dto = CompanyDetailDto.From(company);

        Assert.Equal("MapCorp", dto.Name);
        Assert.Equal("TAX-MAP", dto.TaxCode);
        Assert.Equal("https://logo.png", dto.LogoUrl);
        Assert.Equal("https://map.co", dto.Website);
        Assert.Equal("A description", dto.Description);
        Assert.Equal("123 Street", dto.Address);
        Assert.Equal("Technology", dto.Industry);
        Assert.Equal("50-200", dto.Size);
        Assert.Equal(_ownerId, dto.CreatedBy);
        Assert.False(dto.Verified); // default
    }
}

/// <summary>
/// Internal test invoker — calls CompanyEndpoints private static methods via reflection.
/// Mirrors the pattern used in JobCrudTests to keep tests isolated from the HTTP pipeline.
/// </summary>
internal static class CompanyEndpointsInvoker
{
    private static readonly System.Reflection.MethodInfo _createCompany;
    private static readonly System.Reflection.MethodInfo _getCompanies;
    private static readonly System.Reflection.MethodInfo _getCompanyById;
    private static readonly System.Reflection.MethodInfo _updateCompany;

    static CompanyEndpointsInvoker()
    {
        var type = typeof(CompanyEndpoints);
        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;

        _createCompany = type.GetMethod("CreateCompany", flags)!;
        _getCompanies = type.GetMethod("GetCompanies", flags)!;
        _getCompanyById = type.GetMethod("GetCompanyById", flags)!;
        _updateCompany = type.GetMethod("UpdateCompany", flags)!;
    }

    public static async Task<IResult> CreateCompany(
        CompanyCreateDto dto, JobDbContext db, HttpContext ctx) =>
        await (Task<IResult>)_createCompany.Invoke(null, [dto, db, ctx])!;

    public static async Task<IResult> GetCompanies(
        JobDbContext db, string? q, string? search, int page, int size) =>
        await (Task<IResult>)_getCompanies.Invoke(null, [db, q, search, page, size])!;

    public static async Task<IResult> GetCompanyById(Guid id, JobDbContext db) =>
        await (Task<IResult>)_getCompanyById.Invoke(null, [id, db])!;

    public static async Task<IResult> UpdateCompany(
        Guid id, CompanyUpdateDto dto, JobDbContext db, HttpContext ctx) =>
        await (Task<IResult>)_updateCompany.Invoke(null, [id, dto, db, ctx])!;
}
