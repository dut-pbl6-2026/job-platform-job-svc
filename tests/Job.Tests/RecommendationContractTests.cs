using System.Text.Json;
using Job.Api.DTOs;
using Job.Core.Recommendations;

namespace Job.Tests;

/// <summary>
/// Schema contract for <c>GET /api/jobs/recommended</c> (README "Job recommendations"). Pins the exact
/// camelCase field sets, JSON value kinds and the <c>strategy</c> enum values that clients depend on.
/// </summary>
public class RecommendationContractTests
{
    public static readonly string[] ResponseFields = ["items", "total", "isCached", "generatedAt"];

    public static readonly string[] ItemFields =
    [
        "id", "title", "companyId", "company", "categoryId", "category", "location",
        "salaryMin", "salaryMax", "salaryCurrency", "employmentType", "experienceLevel",
        "createdAt", "strategy", "reason",
    ];

    public static readonly string[] RefFields = ["id", "name"];

    [Fact]
    public void RecommendationResponse_SerializesExactEnvelopeFields()
    {
        var json = Serialize(new RecommendationResponse([BuildItem()], 1, false, DateTime.UtcNow));

        Assert.Equal(ResponseFields.Order(), PropertyNames(json).Order());
        Assert.Equal(JsonValueKind.Number, json.GetProperty("total").ValueKind);
        Assert.Equal(JsonValueKind.False, json.GetProperty("isCached").ValueKind);
        Assert.Equal(JsonValueKind.Array, json.GetProperty("items").ValueKind);
    }

    [Fact]
    public void RecommendedJobDto_SerializesExactItemFields_WithoutDescriptionOrScore()
    {
        var json = Serialize(BuildItem());

        Assert.Equal(ItemFields.Order(), PropertyNames(json).Order());
        Assert.DoesNotContain("description", PropertyNames(json));
        Assert.DoesNotContain("score", PropertyNames(json));
        Assert.DoesNotContain("companyName", PropertyNames(json));
        Assert.DoesNotContain("categoryName", PropertyNames(json));
    }

    [Fact]
    public void RecommendedJobDto_ValueKindsMatchContract()
    {
        var json = Serialize(BuildItem());

        Assert.Equal(JsonValueKind.String, json.GetProperty("id").ValueKind);
        Assert.Equal(JsonValueKind.String, json.GetProperty("companyId").ValueKind);
        Assert.Equal(JsonValueKind.Object, json.GetProperty("company").ValueKind);
        Assert.Equal(JsonValueKind.Object, json.GetProperty("category").ValueKind);
        Assert.Equal(JsonValueKind.Number, json.GetProperty("salaryMin").ValueKind);
        Assert.Equal(JsonValueKind.String, json.GetProperty("salaryCurrency").ValueKind);
        Assert.Equal(JsonValueKind.String, json.GetProperty("createdAt").ValueKind);
        Assert.Equal(JsonValueKind.String, json.GetProperty("strategy").ValueKind);
        Assert.Equal(JsonValueKind.String, json.GetProperty("reason").ValueKind);
    }

    [Fact]
    public void RecommendedJobDto_NestedRefsHaveExactIdAndNameFields()
    {
        var json = Serialize(BuildItem());

        Assert.Equal(RefFields.Order(), PropertyNames(json.GetProperty("company")).Order());
        Assert.Equal(RefFields.Order(), PropertyNames(json.GetProperty("category")).Order());
    }

    [Fact]
    public void RecommendedJobDto_NullCategory_SerializesCategoryIdAndCategoryAsNull()
    {
        var item = BuildItem() with { CategoryId = null, Category = null };

        var json = Serialize(item);

        Assert.Equal(JsonValueKind.Null, json.GetProperty("categoryId").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("category").ValueKind);
        Assert.Equal(ItemFields.Order(), PropertyNames(json).Order());
    }

    [Fact]
    public void RecommendationStrategy_HasExactlyTheContractValues()
    {
        // Clients branch on these strings; adding or renaming a strategy is a contract change.
        Assert.Equal(
            ["Collaborative", "Content", "Popular"],
            Enum.GetNames<RecommendationStrategy>().Order().ToArray());
    }

    [Theory]
    [InlineData(RecommendationStrategy.Collaborative, "Collaborative")]
    [InlineData(RecommendationStrategy.Content, "Content")]
    [InlineData(RecommendationStrategy.Popular, "Popular")]
    public void RecommendedJobDto_StrategyIsSerializedAsPascalCaseName(RecommendationStrategy strategy, string expected)
    {
        var item = BuildItem() with { Strategy = strategy.ToString() };

        Assert.Equal(expected, Serialize(item).GetProperty("strategy").GetString());
    }

    private static RecommendedJobDto BuildItem() => new(
        Guid.NewGuid(),
        "Lập trình viên .NET",
        Guid.NewGuid(),
        new ResourceRefDto(Guid.NewGuid(), "Công ty ABC"),
        Guid.NewGuid(),
        new ResourceRefDto(Guid.NewGuid(), "IT"),
        "Đà Nẵng",
        15_000_000m,
        25_000_000m,
        "VND",
        "FullTime",
        "Mid",
        DateTime.UtcNow,
        RecommendationStrategy.Collaborative.ToString(),
        "Người ứng tuyển cũng ứng tuyển việc này");

    private static JsonElement Serialize<T>(T value) =>
        JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static List<string> PropertyNames(JsonElement element) =>
        element.EnumerateObject().Select(p => p.Name).ToList();
}
