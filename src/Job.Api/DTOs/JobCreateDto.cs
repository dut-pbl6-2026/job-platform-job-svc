namespace Job.Api.DTOs;

/// <summary>Payload for POST /api/jobs — Recruiter only.</summary>
public record JobCreateDto(
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
