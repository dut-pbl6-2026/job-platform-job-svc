using JobPosting = Job.Core.Entities.Job;

namespace Job.Api.DTOs;

/// <summary>
/// Minimal public job row for a company page — <c>GET /api/companies/{id}/jobs</c>.
/// Mirrors the fields the public job card needs (same naming as <see cref="JobDetailDto"/>) and nothing
/// internal: no description, no recruiter id, no status, no moderation data.
/// </summary>
public record PublicJobSummaryDto(
    Guid Id,
    string Title,
    Guid CompanyId,
    string? CompanyName,
    string Location,
    decimal? SalaryMin,
    decimal? SalaryMax,
    string SalaryCurrency,
    Guid? CategoryId,
    string? CategoryName,
    string EmploymentType,
    string ExperienceLevel,
    DateTime CreatedAt)
{
    public static PublicJobSummaryDto From(JobPosting job) => new(
        job.Id,
        job.Title,
        job.CompanyId,
        job.Company?.Name,
        job.Location,
        job.SalaryMin,
        job.SalaryMax,
        job.SalaryCurrency,
        job.CategoryId,
        job.Category?.Name,
        job.EmploymentType,
        job.ExperienceLevel,
        job.CreatedAt);
}
