using System.Text.Json;
using Job.Api.DTOs;
using Job.Core.Entities;

namespace Job.Tests;

/// <summary>
/// Schema contract for company responses (GET /api/companies, GET /api/companies/{id}). Pins the exact
/// camelCase field set and that the internal <c>createdBy</c> ownership id is never exposed.
/// </summary>
public class CompanyContractTests
{
    public static readonly string[] CompanyDetailFields =
    [
        "id", "name", "taxCode", "verified", "logoUrl", "website", "description",
        "address", "industry", "size", "createdAt", "updatedAt",
    ];

    public static readonly string[] PaginatedFields = ["items", "total", "page", "size", "totalPages"];

    [Fact]
    public void CompanyDetailDto_SerializesExactContractFields()
    {
        var json = Serialize(CompanyDetailDto.From(BuildCompany()));

        Assert.Equal(CompanyDetailFields.Order(), PropertyNames(json).Order());
    }

    [Fact]
    public void CompanyDetailDto_NeverExposesCreatedBy()
    {
        var json = Serialize(CompanyDetailDto.From(BuildCompany()));

        Assert.DoesNotContain("createdBy", PropertyNames(json), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void CompanyDetailDto_ValueKindsMatchContract()
    {
        var json = Serialize(CompanyDetailDto.From(BuildCompany()));

        Assert.Equal(JsonValueKind.String, json.GetProperty("id").ValueKind);
        Assert.Equal(JsonValueKind.String, json.GetProperty("name").ValueKind);
        Assert.Equal(JsonValueKind.True, json.GetProperty("verified").ValueKind);
        Assert.Equal(JsonValueKind.String, json.GetProperty("createdAt").ValueKind);
        Assert.Equal(JsonValueKind.String, json.GetProperty("updatedAt").ValueKind);
    }

    [Fact]
    public void CompanyDetailDto_OptionalFieldsSerializeAsNull()
    {
        var company = new Company("Bare Corp", Guid.NewGuid());

        var json = Serialize(CompanyDetailDto.From(company));

        Assert.Equal(JsonValueKind.Null, json.GetProperty("taxCode").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("logoUrl").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("size").ValueKind);
    }

    [Fact]
    public void PaginatedCompanies_SerializesEnvelopeWithCompanyItems()
    {
        var page = new PaginatedResponse<CompanyDetailDto>(
            [CompanyDetailDto.From(BuildCompany())], 1, 1, 10, 1);

        var json = Serialize(page);

        Assert.Equal(PaginatedFields.Order(), PropertyNames(json).Order());
        Assert.Equal(CompanyDetailFields.Order(), PropertyNames(json.GetProperty("items")[0]).Order());
    }

    private static Company BuildCompany()
    {
        var company = new Company("Công ty ABC", Guid.NewGuid(), taxCode: "0101234567");
        company.Verify();
        return company;
    }

    private static JsonElement Serialize<T>(T value) =>
        JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static List<string> PropertyNames(JsonElement element) =>
        element.EnumerateObject().Select(p => p.Name).ToList();
}
