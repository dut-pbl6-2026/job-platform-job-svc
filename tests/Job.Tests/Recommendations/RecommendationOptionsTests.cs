using Job.Infrastructure.Recommendations;
using Microsoft.Extensions.Configuration;

namespace Job.Tests.Recommendations;

/// <summary>PBL6-10: recommendation settings come from env vars and fail fast when invalid.</summary>
public sealed class RecommendationOptionsTests
{
    [Fact]
    public void Unset_UsesDocumentedDefaults()
    {
        var options = Bind(new Dictionary<string, string?>());

        Assert.Equal(6, options.CacheTtlHours);
        Assert.Equal(15, options.PopularTtlMinutes);
        Assert.Equal(500, options.MaxNeighbors);
        Assert.Equal(1, options.MinCooccurrence);
        Assert.False(options.BackfillEnabled);
        Assert.Null(options.AppServiceUrl);
        Assert.Empty(options.Validate());
    }

    [Fact]
    public void EnvValues_AreBound()
    {
        var options = Bind(new Dictionary<string, string?>
        {
            ["RECOMMENDATION_CACHE_TTL_HOURS"] = "2",
            ["RECOMMENDATION_BACKFILL_ENABLED"] = "true",
            ["APP_SERVICE_URL"] = "http://app:5004",
            ["INTERNAL_SERVICE_TOKEN"] = "secret",
        });

        Assert.Equal(2, options.CacheTtlHours);
        Assert.True(options.BackfillEnabled);
        Assert.Equal("http://app:5004", options.AppServiceUrl);
        Assert.Equal("secret", options.InternalServiceToken);
        Assert.Empty(options.Validate());
    }

    [Theory]
    [InlineData("RECOMMENDATION_CACHE_TTL_HOURS", "abc")]
    [InlineData("RECOMMENDATION_CACHE_TTL_HOURS", "0")]
    [InlineData("RECOMMENDATION_MAX_NEIGHBORS", "-1")]
    [InlineData("RECOMMENDATION_MIN_COOCCURRENCE", "0")]
    [InlineData("APP_SERVICE_URL", "app:5004")]
    [InlineData("APP_SERVICE_URL", "ftp://app")]
    public void InvalidValue_FailsValidation(string key, string value)
    {
        var options = Bind(new Dictionary<string, string?> { [key] = value });

        Assert.NotEmpty(options.Validate());
    }

    private static RecommendationOptions Bind(Dictionary<string, string?> values)
    {
        var options = new RecommendationOptions();
        RecommendationOptions.Bind(options, new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        return options;
    }
}
