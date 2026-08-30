using Job.Core.Entities;

namespace Job.Api.DTOs;

/// <summary>Full company detail response — GET /api/companies/{id}.</summary>
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
    Guid CreatedBy,
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
        c.CreatedBy,
        c.CreatedAt,
        c.UpdatedAt
    );
}
