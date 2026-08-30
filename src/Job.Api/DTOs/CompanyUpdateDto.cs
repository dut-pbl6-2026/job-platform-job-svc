namespace Job.Api.DTOs;

/// <summary>Payload for PUT /api/companies/{id} — Recruiter only.</summary>
public record CompanyUpdateDto(
    string Name,
    string? TaxCode = null,
    string? LogoUrl = null,
    string? Website = null,
    string? Description = null,
    string? Address = null,
    string? Industry = null,
    string? Size = null
);
