using System.Net;
using System.Text.Json;

namespace Job.Tests.Recommendations;

/// <summary>
/// PBL6-10 slice 5: GET /api/jobs/recommended over HTTP (TestServer). Contract per
/// api-conventions.md: Problem Details for 401/403/400, nested company/category, cache flag.
/// </summary>
public sealed class RecommendationEndpointTests
{
    private static readonly Guid It = RecommendationTestHost.ItCategory;
    private static readonly Guid Finance = RecommendationTestHost.FinanceCategory;

    [Fact]
    public async Task NoIdentity_Returns401ProblemDetails()
    {
        await using var host = await RecommendationTestHost.StartAsync();

        var response = await host.Client.GetAsync("/api/jobs/recommended");

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("Recruiter")]
    [InlineData("Admin")]
    public async Task OtherRole_Returns403ProblemDetails(string role)
    {
        await using var host = await RecommendationTestHost.StartAsync();

        var response = await host.GetRecommendedAsync(Guid.NewGuid(), role: role);

        await AssertProblemAsync(response, HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("?limit=0")]
    [InlineData("?limit=11")]
    [InlineData("?limit=abc")]
    [InlineData("?limit=-3")]
    public async Task InvalidLimit_Returns400WithLimitError(string query)
    {
        await using var host = await RecommendationTestHost.StartAsync();

        var response = await host.GetRecommendedAsync(Guid.NewGuid(), query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await RecommendationTestHost.ReadJsonAsync(response);
        Assert.Equal(400, body.GetProperty("status").GetInt32());
        Assert.True(body.GetProperty("errors").TryGetProperty("limit", out _));
    }

    [Fact]
    public async Task InvalidRefresh_Returns400WithRefreshError()
    {
        await using var host = await RecommendationTestHost.StartAsync();

        var response = await host.GetRecommendedAsync(Guid.NewGuid(), "?refresh=maybe");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await RecommendationTestHost.ReadJsonAsync(response);
        Assert.True(body.GetProperty("errors").TryGetProperty("refresh", out _));
    }

    [Fact]
    public async Task Success_ReturnsContractShape()
    {
        await using var host = await RecommendationTestHost.StartAsync();
        var jobId = await host.CreateJobAsync("Lập trình viên .NET", It, "Đà Nẵng");
        var noCategory = await host.CreateJobAsync("Nhân viên tổng hợp", null, "Huế");

        var response = await host.GetRecommendedAsync(Guid.NewGuid());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await RecommendationTestHost.ReadJsonAsync(response);
        Assert.Equal(2, body.GetProperty("total").GetInt32());
        Assert.False(body.GetProperty("isCached").GetBoolean());
        Assert.EndsWith("Z", body.GetProperty("generatedAt").GetString());

        var items = body.GetProperty("items").EnumerateArray().ToList();
        var item = items.Single(i => i.GetProperty("id").GetGuid() == jobId);
        Assert.Equal("Lập trình viên .NET", item.GetProperty("title").GetString());
        Assert.Equal(host.CompanyId, item.GetProperty("companyId").GetGuid());
        Assert.Equal(host.CompanyId, item.GetProperty("company").GetProperty("id").GetGuid());
        Assert.StartsWith("Công ty ABC", item.GetProperty("company").GetProperty("name").GetString());
        Assert.Equal(It, item.GetProperty("category").GetProperty("id").GetGuid());
        Assert.Equal("IT", item.GetProperty("category").GetProperty("name").GetString());
        Assert.Equal("Đà Nẵng", item.GetProperty("location").GetString());
        Assert.Equal(15_000_000m, item.GetProperty("salaryMin").GetDecimal());
        Assert.Equal("VND", item.GetProperty("salaryCurrency").GetString());
        Assert.Equal("FullTime", item.GetProperty("employmentType").GetString());
        Assert.Equal("Mid", item.GetProperty("experienceLevel").GetString());
        Assert.EndsWith("Z", item.GetProperty("createdAt").GetString());
        Assert.Equal("Popular", item.GetProperty("strategy").GetString());
        Assert.Equal("Đang được nhiều ứng viên quan tâm", item.GetProperty("reason").GetString());
        foreach (var hidden in new[] { "companyName", "categoryName", "description", "score", "viewCount", "recruiterId" })
            Assert.False(item.TryGetProperty(hidden, out _), $"unexpected field {hidden}");

        var uncategorized = items.Single(i => i.GetProperty("id").GetGuid() == noCategory);
        Assert.Equal(JsonValueKind.Null, uncategorized.GetProperty("categoryId").ValueKind);
        Assert.Equal(JsonValueKind.Null, uncategorized.GetProperty("category").ValueKind);
    }

    [Fact]
    public async Task Limit_IsApplied()
    {
        await using var host = await RecommendationTestHost.StartAsync();
        for (var i = 0; i < 4; i++)
            await host.CreateJobAsync($"Việc làm {i}", It, "Đà Nẵng");

        var body = await RecommendationTestHost.ReadJsonAsync(await host.GetRecommendedAsync(Guid.NewGuid(), "?limit=2"));

        Assert.Equal(2, body.GetProperty("items").GetArrayLength());
        Assert.Equal(2, body.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task SecondCall_IsServedFromCache()
    {
        await using var host = await RecommendationTestHost.StartAsync();
        await host.CreateJobAsync("Lập trình viên .NET", It, "Đà Nẵng");
        var user = Guid.NewGuid();

        var first = await RecommendationTestHost.ReadJsonAsync(await host.GetRecommendedAsync(user));
        var second = await RecommendationTestHost.ReadJsonAsync(await host.GetRecommendedAsync(user));

        Assert.False(first.GetProperty("isCached").GetBoolean());
        Assert.True(second.GetProperty("isCached").GetBoolean());
        Assert.Equal(first.GetProperty("generatedAt").GetString(), second.GetProperty("generatedAt").GetString());
    }

    [Fact]
    public async Task NewApplicationEvent_ClearsTheUsersCache()
    {
        await using var host = await RecommendationTestHost.StartAsync();
        var j1 = await host.CreateJobAsync("Lập trình viên .NET", It, "Đà Nẵng");
        var j2 = await host.CreateJobAsync("Kế toán", Finance, "Hà Nội");
        var user = Guid.NewGuid();
        var before = await RecommendationTestHost.ReadJsonAsync(await host.GetRecommendedAsync(user));
        Assert.Contains(j1, Ids(before));

        await host.Consumer.ApplyAsync(user, j1);
        var after = await RecommendationTestHost.ReadJsonAsync(await host.GetRecommendedAsync(user));

        Assert.False(after.GetProperty("isCached").GetBoolean());
        Assert.DoesNotContain(j1, Ids(after));
        Assert.Contains(j2, Ids(after));
    }

    [Fact]
    public async Task Refresh_RecomputesOncePerCooldown()
    {
        await using var host = await RecommendationTestHost.StartAsync();
        await host.CreateJobAsync("Lập trình viên .NET", It, "Đà Nẵng");
        var user = Guid.NewGuid();
        await host.GetRecommendedAsync(user);

        var refreshed = await RecommendationTestHost.ReadJsonAsync(await host.GetRecommendedAsync(user, "?refresh=true"));
        var again = await RecommendationTestHost.ReadJsonAsync(await host.GetRecommendedAsync(user, "?refresh=true"));

        Assert.False(refreshed.GetProperty("isCached").GetBoolean());
        Assert.True(again.GetProperty("isCached").GetBoolean());
    }

    [Fact]
    public async Task JobDeletedAfterCaching_IsFilteredFromCachedResult()
    {
        await using var host = await RecommendationTestHost.StartAsync();
        var kept = await host.CreateJobAsync("Lập trình viên .NET", It, "Đà Nẵng");
        var removed = await host.CreateJobAsync("Kế toán", Finance, "Hà Nội");
        var user = Guid.NewGuid();
        Assert.Contains(removed, Ids(await RecommendationTestHost.ReadJsonAsync(await host.GetRecommendedAsync(user))));

        await host.DeleteJobAsync(removed);
        var cached = await RecommendationTestHost.ReadJsonAsync(await host.GetRecommendedAsync(user));

        Assert.True(cached.GetProperty("isCached").GetBoolean());
        Assert.Equal(new[] { kept }, Ids(cached));
        Assert.Equal(1, cached.GetProperty("total").GetInt32());
    }

    private static List<Guid> Ids(JsonElement body) =>
        body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await RecommendationTestHost.ReadJsonAsync(response);
        Assert.Equal((int)expected, body.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("type").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("title").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("detail").GetString()));
        Assert.False(body.TryGetProperty("message", out _));
    }
}
