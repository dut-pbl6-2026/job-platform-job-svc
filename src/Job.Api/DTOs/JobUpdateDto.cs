namespace Job.Api.DTOs;

/// <summary>Payload for PUT /api/jobs/{id} — Recruiter (owner) only.</summary>
public record JobUpdateDto(
    string Title,
    string Description,
    Guid CompanyId,
    string Location,
    decimal? SalaryMin,
    decimal? SalaryMax,
    string? SalaryCurrency,
    Guid? CategoryId,
    string? Requirements,
    string? Benefits,
    string? EmploymentType,
    string? ExperienceLevel);
