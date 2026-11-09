namespace Job.Api.DTOs;

/// <summary>
/// One recommended job — <c>GET /api/jobs/recommended</c> (PBL6-10, contract in README).
/// Nested <c>company</c>/<c>category</c> per api-conventions §3.1, no flat <c>companyName</c>,
/// no <c>description</c>, no internal score. <c>Strategy</c> is <c>Collaborative | Content | Popular</c>;
/// <c>Reason</c> is the Vietnamese text clients display.
/// </summary>
public record RecommendedJobDto(
    Guid Id,
    string Title,
    Guid CompanyId,
    ResourceRefDto Company,
    Guid? CategoryId,
    ResourceRefDto? Category,
    string Location,
    decimal? SalaryMin,
    decimal? SalaryMax,
    string SalaryCurrency,
    string EmploymentType,
    string ExperienceLevel,
    DateTime CreatedAt,
    string Strategy,
    string Reason);
