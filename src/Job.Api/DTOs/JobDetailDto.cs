using JobPosting = Job.Core.Entities.Job;

namespace Job.Api.DTOs;

/// <summary>Full job detail response — GET /api/jobs/{id}.</summary>
public record JobDetailDto(
    Guid Id,
    string Title,
    string Description,
    Guid CompanyId,
    string? CompanyName,
    string Location,
    decimal? SalaryMin,
    decimal? SalaryMax,
    string SalaryCurrency,
    Guid? CategoryId,
    string? CategoryName,
    string? Requirements,
    string? Benefits,
    string EmploymentType,
    string ExperienceLevel,
    Guid RecruiterId,
    string Status,
    int ViewCount,
    DateTime CreatedAt,
    DateTime UpdatedAt)
{
    /// <summary>Map from domain entity — includes navigation properties.</summary>
    public static JobDetailDto From(JobPosting job) => new(
        job.Id,
        job.Title,
        job.Description,
        job.CompanyId,
        job.Company?.Name,
        job.Location,
        job.SalaryMin,
        job.SalaryMax,
        job.SalaryCurrency,
        job.CategoryId,
        job.Category?.Name,
        job.Requirements,
        job.Benefits,
        job.EmploymentType,
        job.ExperienceLevel,
        job.RecruiterId,
        job.Status.ToString(),
        job.ViewCount,
        job.CreatedAt,
        job.UpdatedAt);
}
