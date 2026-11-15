using System.Text.Json;
using Job.Api.DTOs;
using Job.Core.Entities;
using JobPosting = Job.Core.Entities.Job;

namespace Job.Tests;

/// <summary>
/// Schema contract for job responses (docs/api-conventions.md: camelCase, flat companyName/categoryName).
/// Asserts exact field names and JSON value kinds, so an added, renamed or removed field fails a test.
/// </summary>
public class JobContractTests
{
    /// <summary>Exact field set of GET /api/jobs/{id} and each item of GET /api/jobs/recruiter.</summary>
    public static readonly string[] JobDetailFields =
    [
        "id", "title", "description", "companyId", "companyName", "location",
        "salaryMin", "salaryMax", "salaryCurrency", "categoryId", "categoryName",
        "requirements", "benefits", "employmentType", "experienceLevel",
        "status", "viewCount", "createdAt", "updatedAt",
    ];

    /// <summary>Exact field set of the paginated list envelope (PaginatedResponse).</summary>
    public static readonly string[] PaginatedFields = ["items", "total", "page", "size", "totalPages"];

    [Fact]
    public void JobDetailDto_SerializesExactContractFields()
    {
        var json = Serialize(BuildDetail());

        Assert.Equal(JobDetailFields.Order(), PropertyNames(json).Order());
    }

    [Fact]
    public void JobDetailDto_DoesNotExposeRecruiterId()
    {
        var json = Serialize(BuildDetail());

        Assert.False(json.TryGetProperty("recruiterId", out _));
        Assert.DoesNotContain("recruiterId", PropertyNames(json), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void JobDetailDto_ValueKindsMatchContract()
    {
        var json = Serialize(BuildDetail());

        Assert.Equal(JsonValueKind.String, json.GetProperty("id").ValueKind);
        Assert.Equal(JsonValueKind.String, json.GetProperty("companyId").ValueKind);
        Assert.Equal(JsonValueKind.String, json.GetProperty("categoryId").ValueKind);
        Assert.Equal(JsonValueKind.String, json.GetProperty("status").ValueKind);
        Assert.Equal(JsonValueKind.String, json.GetProperty("salaryCurrency").ValueKind);
        Assert.Equal(JsonValueKind.Number, json.GetProperty("salaryMin").ValueKind);
        Assert.Equal(JsonValueKind.Number, json.GetProperty("salaryMax").ValueKind);
        Assert.Equal(JsonValueKind.Number, json.GetProperty("viewCount").ValueKind);
        Assert.Equal(JsonValueKind.String, json.GetProperty("createdAt").ValueKind);
        Assert.Equal(JsonValueKind.String, json.GetProperty("updatedAt").ValueKind);
        Assert.Equal("Active", json.GetProperty("status").GetString());
    }

    [Fact]
    public void JobDetailDto_NullableFieldsSerializeAsNull()
    {
        var job = new JobPosting("Dev", "Desc", Guid.NewGuid(), "Da Nang", Guid.NewGuid());

        var json = Serialize(JobDetailDto.From(job));

        Assert.Equal(JsonValueKind.Null, json.GetProperty("companyName").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("categoryId").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("salaryMin").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("requirements").ValueKind);
    }

    [Fact]
    public void PaginatedJobs_SerializesEnvelopeWithJobDetailItems()
    {
        var page = new PaginatedResponse<JobDetailDto>([JobDetailDto.From(new JobPosting("Dev", "Desc", Guid.NewGuid(), "Da Nang", Guid.NewGuid()))], 1, 1, 10, 1);

        var json = Serialize(page);

        Assert.Equal(PaginatedFields.Order(), PropertyNames(json).Order());
        Assert.Equal(JobDetailFields.Order(), PropertyNames(json.GetProperty("items")[0]).Order());
        Assert.Equal(JsonValueKind.Number, json.GetProperty("totalPages").ValueKind);
    }

    private static JobDetailDto BuildDetail()
    {
        var job = new JobPosting(
            "Backend Developer", "Build APIs", Guid.NewGuid(), "Da Nang", Guid.NewGuid(),
            salaryMin: 15_000_000, salaryMax: 25_000_000, categoryId: Guid.NewGuid());
        return JobDetailDto.From(job);
    }

    private static JsonElement Serialize<T>(T value) =>
        JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static List<string> PropertyNames(JsonElement element) =>
        element.EnumerateObject().Select(p => p.Name).ToList();
}
