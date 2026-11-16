using Job.Core.Entities;

namespace Job.Api.DTOs;

/// <summary>
/// Company detail for the signed-in recruiter — <c>GET /api/companies/mine</c>.
/// Same public fields as <see cref="CompanyDetailDto"/> plus <see cref="IsCompanyAdmin"/>:
/// true when the caller is the company creator (<c>Company.CreatedBy</c>, the company admin),
/// false for every other recruiter of the company (member).
/// <c>CreatedBy</c> itself is never exposed.
/// </summary>
public record CompanyMineDto(
    Guid Id,
    string Name,
    string? TaxCode,
    bool Verified,
    string? LogoUrl,
    string? Website,
    string? Description,
    string? Address,
    string? Industry,
    string? Size,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    bool IsCompanyAdmin)
{
    /// <summary>Map from the domain entity, comparing <c>CreatedBy</c> to the caller's user id.</summary>
    public static CompanyMineDto From(Company c, Guid userId) => new(
        c.Id,
        c.Name,
        c.TaxCode,
        c.Verified,
        c.LogoUrl,
        c.Website,
        c.Description,
        c.Address,
        c.Industry,
        c.Size,
        c.CreatedAt,
        c.UpdatedAt,
        c.CreatedBy == userId);
}
