using Microsoft.Extensions.Configuration;

namespace Job.Infrastructure.Recommendations;

/// <summary>
/// Recommendation settings (PBL6-10), bound from flat env vars and validated at startup.
/// Nothing is hard-coded except the documented defaults.
/// </summary>
public sealed class RecommendationOptions
{
    /// <summary>Per-user cache lifetime (RECOMMEND-01-05).</summary>
    public int CacheTtlHours { get; set; } = 6;

    /// <summary>Lifetime of the shared popular list.</summary>
    public int PopularTtlMinutes { get; set; } = 15;

    /// <summary>Most recent neighbour users considered for collaborative filtering.</summary>
    public int MaxNeighbors { get; set; } = 500;

    /// <summary>Minimum common users for a job pair to count.</summary>
    public int MinCooccurrence { get; set; } = 1;

    /// <summary>Run the one-time startup backfill from app-svc.</summary>
    public bool BackfillEnabled { get; set; }

    /// <summary>app-svc base URL for the backfill.</summary>
    public string? AppServiceUrl { get; set; }

    /// <summary>Bearer token for app-svc internal endpoints.</summary>
    public string? InternalServiceToken { get; set; }

    /// <summary>Reads the env-style keys; unset values keep the default.</summary>
    public static void Bind(RecommendationOptions o, IConfiguration c)
    {
        o.CacheTtlHours = Int(c, "RECOMMENDATION_CACHE_TTL_HOURS", o.CacheTtlHours);
        o.PopularTtlMinutes = Int(c, "RECOMMENDATION_POPULAR_TTL_MINUTES", o.PopularTtlMinutes);
        o.MaxNeighbors = Int(c, "RECOMMENDATION_MAX_NEIGHBORS", o.MaxNeighbors);
        o.MinCooccurrence = Int(c, "RECOMMENDATION_MIN_COOCCURRENCE", o.MinCooccurrence);
        o.BackfillEnabled = bool.TryParse(c["RECOMMENDATION_BACKFILL_ENABLED"], out var enabled) && enabled;
        o.AppServiceUrl = Text(c, "APP_SERVICE_URL");
        o.InternalServiceToken = Text(c, "INTERNAL_SERVICE_TOKEN");
    }

    /// <summary>Returns the validation errors (empty when valid).</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (CacheTtlHours is < 1 or > 168)
            errors.Add("RECOMMENDATION_CACHE_TTL_HOURS must be 1-168.");
        if (PopularTtlMinutes is < 1 or > 1440)
            errors.Add("RECOMMENDATION_POPULAR_TTL_MINUTES must be 1-1440.");
        if (MaxNeighbors is < 1 or > 10_000)
            errors.Add("RECOMMENDATION_MAX_NEIGHBORS must be 1-10000.");
        if (MinCooccurrence is < 1 or > 1_000)
            errors.Add("RECOMMENDATION_MIN_COOCCURRENCE must be 1-1000.");
        if (AppServiceUrl is not null
            && (!Uri.TryCreate(AppServiceUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
            errors.Add("APP_SERVICE_URL must be an absolute http(s) URL.");
        return errors;
    }

    // Unset → default. Set but not a number → 0, which fails Validate() at startup.
    private static int Int(IConfiguration c, string key, int fallback) =>
        string.IsNullOrWhiteSpace(c[key]) ? fallback : int.TryParse(c[key], out var value) ? value : 0;

    private static string? Text(IConfiguration c, string key) =>
        string.IsNullOrWhiteSpace(c[key]) ? null : c[key]!.Trim();
}
