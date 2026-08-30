namespace Job.Api.DTOs;

/// <summary>Payload for POST /api/companies — Recruiter only.</summary>
public record CompanyCreateDto(
    string Name,
    string? TaxCode = null,
    string? LogoUrl = null,
    string? Website = null,
    string? Description = null,
    string? Address = null,
    string? Industry = null,
    string? Size = null
);
