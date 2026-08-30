using SharedKernel;

namespace Job.Core.Entities;

public class Job : Entity
{
    public string Title { get; private set; } = "";
    public string Description { get; private set; } = "";
    public Guid CompanyId { get; private set; }
    public Company? Company { get; private set; }
    public string Location { get; private set; } = "";
    public decimal? SalaryMin { get; private set; }
    public decimal? SalaryMax { get; private set; }
    public string SalaryCurrency { get; private set; } = "VND";
    public Guid? CategoryId { get; private set; }
    public Category? Category { get; private set; }
    public string? Requirements { get; private set; }
    public string? Benefits { get; private set; }
    public string EmploymentType { get; private set; } = "FullTime";
    public string ExperienceLevel { get; private set; } = "Entry";
    public Guid RecruiterId { get; private set; }
    public JobStatus Status { get; private set; } = JobStatus.Active;
    public int ViewCount { get; private set; }

    private Job() { }

    public Job(
        string title,
        string description,
        Guid companyId,
        string location,
        Guid recruiterId,
        decimal? salaryMin = null,
        decimal? salaryMax = null,
        string salaryCurrency = "VND",
        Guid? categoryId = null,
        string? requirements = null,
        string? benefits = null,
        string employmentType = "FullTime",
        string experienceLevel = "Entry")
    {
        // M1: guard against null before Trim
        ArgumentException.ThrowIfNullOrWhiteSpace(title, nameof(title));
        ArgumentException.ThrowIfNullOrWhiteSpace(description, nameof(description));
        ArgumentException.ThrowIfNullOrWhiteSpace(location, nameof(location));
        ArgumentException.ThrowIfNullOrWhiteSpace(employmentType, nameof(employmentType));
        ArgumentException.ThrowIfNullOrWhiteSpace(experienceLevel, nameof(experienceLevel));

        // M3: salary range validation
        ValidateSalaryRange(salaryMin, salaryMax);

        Title = title.Trim();
        Description = description.Trim();
        CompanyId = companyId;
        Location = location.Trim();
        RecruiterId = recruiterId;
        SalaryMin = salaryMin;
        SalaryMax = salaryMax;
        SalaryCurrency = NormalizeCurrency(salaryCurrency);
        CategoryId = categoryId;
        Requirements = NormalizeOptional(requirements);
        Benefits = NormalizeOptional(benefits);
        EmploymentType = employmentType.Trim();
        ExperienceLevel = experienceLevel.Trim();
    }

    public void Update(
        string title,
        string description,
        Guid companyId,
        string location,
        decimal? salaryMin,
        decimal? salaryMax,
        string salaryCurrency,
        Guid? categoryId,
        string? requirements,
        string? benefits,
        string employmentType,
        string experienceLevel)
    {
        // M1: guard against null before Trim
        ArgumentException.ThrowIfNullOrWhiteSpace(title, nameof(title));
        ArgumentException.ThrowIfNullOrWhiteSpace(description, nameof(description));
        ArgumentException.ThrowIfNullOrWhiteSpace(location, nameof(location));
        ArgumentException.ThrowIfNullOrWhiteSpace(employmentType, nameof(employmentType));
        ArgumentException.ThrowIfNullOrWhiteSpace(experienceLevel, nameof(experienceLevel));

        // M3: salary range validation
        ValidateSalaryRange(salaryMin, salaryMax);

        Title = title.Trim();
        Description = description.Trim();
        CompanyId = companyId;
        Location = location.Trim();
        SalaryMin = salaryMin;
        SalaryMax = salaryMax;
        SalaryCurrency = NormalizeCurrency(salaryCurrency);
        CategoryId = categoryId;
        Requirements = NormalizeOptional(requirements);
        Benefits = NormalizeOptional(benefits);
        EmploymentType = employmentType.Trim();
        ExperienceLevel = experienceLevel.Trim();
        Touch();
    }

    public void Close()
    {
        Status = JobStatus.Closed;
        Touch();
    }

    public void Reopen()
    {
        Status = JobStatus.Active;
        Touch();
    }

    public void SoftDelete()
    {
        Status = JobStatus.Deleted;
        Touch();
    }

    public void IncrementView()
    {
        ViewCount++;
    }

    // M2: fix NormalizeCurrency — check null/whitespace before Trim
    private static string NormalizeCurrency(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "VND";
        var trimmed = value.Trim().ToUpperInvariant();
        return string.IsNullOrWhiteSpace(trimmed) ? "VND" : trimmed;
    }

    // M3: salary range guard — both non-negative and min <= max
    private static void ValidateSalaryRange(decimal? salaryMin, decimal? salaryMax)
    {
        if (salaryMin.HasValue && salaryMin.Value < 0)
            throw new ArgumentOutOfRangeException(nameof(salaryMin), "SalaryMin must be >= 0.");
        if (salaryMax.HasValue && salaryMax.Value < 0)
            throw new ArgumentOutOfRangeException(nameof(salaryMax), "SalaryMax must be >= 0.");
        if (salaryMin.HasValue && salaryMax.HasValue && salaryMin.Value > salaryMax.Value)
            throw new ArgumentException("SalaryMin must be <= SalaryMax.", nameof(salaryMin));
    }

    private static string? NormalizeOptional(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }
}
