using Job.Core.Entities;

namespace Job.Api.DTOs;

/// <summary>Full company detail response — GET /api/companies/{id}.</summary>
/// <remarks>
/// CreatedBy is intentionally omitted: it is an internal ownership Guid used for
/// PUT /api/companies/{id} authorization and is not part of the public company profile
/// per SRS 3-must-have-fr.md:87 / 10-appendices.md:269.
/// </remarks>
public record CompanyDetailDto(
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
    DateTime UpdatedAt
)
{
    /// <summary>Map from domain entity.</summary>
    public static CompanyDetailDto From(Company c) => new(
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
        c.UpdatedAt
    );
}
