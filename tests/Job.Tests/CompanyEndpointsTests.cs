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

    private static HttpContext BuildContext(Guid userId, string role = "Recruiter", Guid? companyId = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Role, role)
        };
        if (companyId is { } id)
            claims.Add(new Claim("company_id", id.ToString()));
        return new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
        };
    }

    private static HttpContext BuildAnonymousContext() =>
        new DefaultHttpContext { User = new ClaimsPrincipal() };

    /// <summary>Job with an explicit CreatedAt so "newest first" is deterministic.</summary>
    private Job.Core.Entities.Job NewJob(Guid companyId, string title, DateTime createdAt)
    {
        var job = new Job.Core.Entities.Job(title, "Desc", companyId, "Da Nang", _ownerId);
        typeof(Job.Core.Entities.Job).GetProperty(nameof(Job.Core.Entities.Job.CreatedAt))!.SetValue(job, createdAt);
        return job;
    }

    // ─── POST /api/companies (SRS AUTH-01-06: a Recruiter always has its Company) ──

    [Fact]
    public async Task CreateCompany_Recruiter_Returns409_AccountAlreadyHasCompany()
    {
        var result = await CompanyEndpointsInvoker.CreateCompany(BuildContext(_ownerId));

        Assert.Equal(409, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.Empty(_db.Companies);
    }

    [Fact]
    public async Task CreateCompany_NonRecruiter_Returns403()
    {
        var result = await CompanyEndpointsInvoker.CreateCompany(BuildContext(_ownerId, "User"));

        Assert.Equal(403, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public async Task CreateCompany_Unauthenticated_Returns401()
    {
        var result = await CompanyEndpointsInvoker.CreateCompany(BuildAnonymousContext());

        Assert.Equal(401, ((IStatusCodeHttpResult)result).StatusCode);
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
        // CreatedBy is intentionally omitted from the public DTO (internal ownership field)
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

    [Fact]
    public async Task UpdateCompany_NameTooLong_Returns400()
    {
        var company = new Company("SomeCorp", _ownerId);
        _db.Companies.Add(company);
        await _db.SaveChangesAsync();

        var dto = new CompanyUpdateDto(new string('A', 257)); // > Company.NameMaxLength (256)
        var ctx = BuildContext(_ownerId);

        var result = await CompanyEndpointsInvoker.UpdateCompany(company.Id, dto, _db, ctx);

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(400, status.StatusCode);

        // Company unchanged
        var unchanged = await _db.Companies.FindAsync(company.Id);
        Assert.Equal("SomeCorp", unchanged!.Name);
    }

    [Fact]
    public async Task UpdateCompany_Admin_Returns403()
    {
        // Admin is not a company owner: the PUT surface is Recruiter owner-only.
        var company = new Company("AdminTargetCorp", _ownerId);
        _db.Companies.Add(company);
        await _db.SaveChangesAsync();

        var dto = new CompanyUpdateDto("AdminRenamed");
        var ctx = BuildContext(_ownerId, "Admin");

        var result = await CompanyEndpointsInvoker.UpdateCompany(company.Id, dto, _db, ctx);

        Assert.Equal(403, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        var unchanged = await _db.Companies.FindAsync(company.Id);
        Assert.Equal("AdminTargetCorp", unchanged!.Name);
    }

    [Fact]
    public async Task UpdateCompany_MemberWithCompanyClaim_Returns403()
    {
        // A member of the company (company_id claim, but not the creator) may only view it.
        var company = new Company("OwnerOnlyCorp", _ownerId);
        _db.Companies.Add(company);
        await _db.SaveChangesAsync();

        var dto = new CompanyUpdateDto("MemberRenamed");
        var ctx = BuildContext(_otherId, companyId: company.Id);

        var result = await CompanyEndpointsInvoker.UpdateCompany(company.Id, dto, _db, ctx);

        Assert.Equal(403, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        var unchanged = await _db.Companies.FindAsync(company.Id);
        Assert.Equal("OwnerOnlyCorp", unchanged!.Name);
    }

    // ─── GET /api/companies/mine ─────────────────────────────────────────────

    [Fact]
    public async Task GetMyCompany_Creator_ReturnsCompanyWithIsCompanyAdminTrue()
    {
        var company = new Company("MyCorp", _ownerId, "TAX-MINE");
        _db.Companies.Add(company);
        await _db.SaveChangesAsync();

        var result = await CompanyEndpointsInvoker.GetMyCompany(
            _db, BuildContext(_ownerId, companyId: company.Id));

        var ok = Assert.IsType<Ok<CompanyMineDto>>(result);
        Assert.Equal(company.Id, ok.Value!.Id);
        Assert.Equal("MyCorp", ok.Value.Name);
        Assert.True(ok.Value.IsCompanyAdmin);
        // CreatedBy is never part of the response surface.
        Assert.DoesNotContain("CreatedBy", typeof(CompanyMineDto).GetProperties().Select(p => p.Name));
    }

    [Fact]
    public async Task GetMyCompany_Member_ReturnsCompanyWithIsCompanyAdminFalse()
    {
        var company = new Company("MyCorp", _ownerId);
        _db.Companies.Add(company);
        await _db.SaveChangesAsync();

        var result = await CompanyEndpointsInvoker.GetMyCompany(
            _db, BuildContext(_otherId, companyId: company.Id));

        var ok = Assert.IsType<Ok<CompanyMineDto>>(result);
        Assert.False(ok.Value!.IsCompanyAdmin);
    }

    [Fact]
    public async Task GetMyCompany_NoCompanyLinked_Returns404()
    {
        var result = await CompanyEndpointsInvoker.GetMyCompany(_db, BuildContext(_otherId));

        Assert.Equal(404, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }

    [Fact]
    public async Task GetMyCompany_OlderTokenWithoutClaim_FallsBackToCreator()
    {
        var company = new Company("LegacyTokenCorp", _ownerId);
        _db.Companies.Add(company);
        await _db.SaveChangesAsync();

        var result = await CompanyEndpointsInvoker.GetMyCompany(_db, BuildContext(_ownerId));

        var ok = Assert.IsType<Ok<CompanyMineDto>>(result);
        Assert.Equal(company.Id, ok.Value!.Id);
        Assert.True(ok.Value.IsCompanyAdmin);
    }

    [Fact]
    public async Task GetMyCompany_NonRecruiter_Returns403()
    {
        var result = await CompanyEndpointsInvoker.GetMyCompany(_db, BuildContext(_ownerId, "User"));

        Assert.Equal(403, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }

    [Fact]
    public async Task GetMyCompany_Anonymous_Returns401()
    {
        var result = await CompanyEndpointsInvoker.GetMyCompany(_db, BuildAnonymousContext());

        Assert.Equal(401, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }

    // ─── GET /api/companies/{id}/jobs ────────────────────────────────────────

    [Fact]
    public async Task GetCompanyJobs_ReturnsOnlyActiveJobs_NewestFirst()
    {
        var company = new Company("JobsCorp", _ownerId);
        _db.Companies.Add(company);
        await _db.SaveChangesAsync();

        var older = NewJob(company.Id, "Older Active", DateTime.UtcNow.AddMinutes(-10));
        var newer = NewJob(company.Id, "Newer Active", DateTime.UtcNow);
        var closed = NewJob(company.Id, "Closed Job", DateTime.UtcNow);
        closed.Close();
        var deleted = NewJob(company.Id, "Deleted Job", DateTime.UtcNow);
        deleted.SoftDelete();
        _db.Jobs.AddRange(older, newer, closed, deleted);
        await _db.SaveChangesAsync();

        var result = await CompanyEndpointsInvoker.GetCompanyJobs(company.Id, _db, page: 0, size: 20);

        var ok = Assert.IsType<Ok<PaginatedResponse<PublicJobSummaryDto>>>(result);
        Assert.Equal(2, ok.Value!.Total);
        Assert.Equal(new[] { newer.Id, older.Id }, ok.Value.Items.Select(j => j.Id));
        Assert.Equal(0, ok.Value.Page);
        Assert.Equal(1, ok.Value.TotalPages);
    }

    [Fact]
    public async Task GetCompanyJobs_DoesNotIncludeJobsOfAnotherCompany()
    {
        var company = new Company("JobsCorp", _ownerId);
        var other = new Company("OtherCorp", _ownerId);
        _db.Companies.AddRange(company, other);
        await _db.SaveChangesAsync();
        _db.Jobs.Add(NewJob(company.Id, "Mine", DateTime.UtcNow));
        _db.Jobs.Add(NewJob(other.Id, "Theirs", DateTime.UtcNow));
        await _db.SaveChangesAsync();

        var result = await CompanyEndpointsInvoker.GetCompanyJobs(company.Id, _db, page: 0, size: 20);

        var ok = Assert.IsType<Ok<PaginatedResponse<PublicJobSummaryDto>>>(result);
        Assert.Equal("Mine", Assert.Single(ok.Value!.Items).Title);
    }

    [Fact]
    public async Task GetCompanyJobs_Paging_IsZeroBasedAndCapped()
    {
        var company = new Company("JobsCorp", _ownerId);
        _db.Companies.Add(company);
        await _db.SaveChangesAsync();
        for (var i = 0; i < 5; i++)
            _db.Jobs.Add(NewJob(company.Id, $"J{i}", DateTime.UtcNow.AddMinutes(i)));
        await _db.SaveChangesAsync();

        var first = Assert.IsType<Ok<PaginatedResponse<PublicJobSummaryDto>>>(
            await CompanyEndpointsInvoker.GetCompanyJobs(company.Id, _db, page: 0, size: 2));
        var second = Assert.IsType<Ok<PaginatedResponse<PublicJobSummaryDto>>>(
            await CompanyEndpointsInvoker.GetCompanyJobs(company.Id, _db, page: 1, size: 2));
        var capped = Assert.IsType<Ok<PaginatedResponse<PublicJobSummaryDto>>>(
            await CompanyEndpointsInvoker.GetCompanyJobs(company.Id, _db, page: -3, size: 500));

        Assert.Equal(5, first.Value!.Total);
        Assert.Equal(3, first.Value.TotalPages);
        Assert.Equal(new[] { "J4", "J3" }, first.Value.Items.Select(j => j.Title));
        Assert.Equal(new[] { "J2", "J1" }, second.Value!.Items.Select(j => j.Title));
        Assert.Equal(0, capped.Value!.Page); // negative page clamps to 0
        Assert.Equal(100, capped.Value.Size); // size caps at 100
        Assert.Equal(5, capped.Value.Items.Count);
    }

    [Fact]
    public async Task GetCompanyJobs_UnknownCompany_Returns404()
    {
        var result = await CompanyEndpointsInvoker.GetCompanyJobs(Guid.NewGuid(), _db, page: 0, size: 20);

        Assert.Equal(404, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
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
        // CreatedBy is intentionally omitted from the public DTO (internal ownership field)
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
    private static readonly System.Reflection.MethodInfo _getMyCompany;
    private static readonly System.Reflection.MethodInfo _getCompanyJobs;

    static CompanyEndpointsInvoker()
    {
        var type = typeof(CompanyEndpoints);
        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;

        _createCompany = type.GetMethod("CreateCompany", flags)!;
        _getCompanies = type.GetMethod("GetCompanies", flags)!;
        _getCompanyById = type.GetMethod("GetCompanyById", flags)!;
        _updateCompany = type.GetMethod("UpdateCompany", flags)!;
        _getMyCompany = type.GetMethod("GetMyCompany", flags)!;
        _getCompanyJobs = type.GetMethod("GetCompanyJobs", flags)!;
    }

    public static Task<IResult> CreateCompany(HttpContext ctx) =>
        Task.FromResult((IResult)_createCompany.Invoke(null, [ctx])!);

    public static async Task<IResult> GetCompanies(
        JobDbContext db, string? q, string? search, int page, int size) =>
        await (Task<IResult>)_getCompanies.Invoke(null, [db, q, search, page, size])!;

    public static async Task<IResult> GetCompanyById(Guid id, JobDbContext db) =>
        await (Task<IResult>)_getCompanyById.Invoke(null, [id, db])!;

    public static async Task<IResult> UpdateCompany(
        Guid id, CompanyUpdateDto dto, JobDbContext db, HttpContext ctx) =>
        await (Task<IResult>)_updateCompany.Invoke(null, [id, dto, db, ctx])!;

    public static async Task<IResult> GetMyCompany(JobDbContext db, HttpContext ctx) =>
        await (Task<IResult>)_getMyCompany.Invoke(null, [db, ctx, CancellationToken.None])!;

    public static async Task<IResult> GetCompanyJobs(
        Guid id, JobDbContext db, int page, int size) =>
        await (Task<IResult>)_getCompanyJobs.Invoke(null, [id, db, page, size, CancellationToken.None])!;
}
