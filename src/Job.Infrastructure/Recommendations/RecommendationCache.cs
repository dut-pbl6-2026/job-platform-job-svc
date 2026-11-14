using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Job.Infrastructure.Recommendations;

/// <summary>
/// In-process cache for recommendations (PBL6-10, RECOMMEND-01-05). Owns its own bounded
/// <see cref="MemoryCache"/> (10 000 entries) so the size limit never affects other caches.
/// Per replica: with more than one replica, invalidation only reaches the replica that
/// consumed the event (one replica today).
/// </summary>
public sealed class RecommendationCache : IDisposable
{
    public const int MaxEntries = 10_000;
    public static readonly TimeSpan RefreshCooldown = TimeSpan.FromSeconds(60);

    private const string PopularKey = "rec:v1:popular";

    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = MaxEntries });
    private readonly RecommendationOptions _options;

    public RecommendationCache(IOptions<RecommendationOptions> options) => _options = options.Value;

    public bool TryGet(Guid userId, out RecommendationSnapshot snapshot)
    {
        if (_cache.TryGetValue(UserKey(userId), out RecommendationSnapshot? cached) && cached is not null)
        {
            snapshot = cached;
            return true;
        }

        snapshot = null!;
        return false;
    }

    public void Set(Guid userId, RecommendationSnapshot snapshot) =>
        _cache.Set(UserKey(userId), snapshot, Entry(TimeSpan.FromHours(_options.CacheTtlHours)));

    /// <summary>Drops the user's cached ranking (called when one of their interactions arrives).</summary>
    public void Invalidate(Guid userId) => _cache.Remove(UserKey(userId));

    /// <summary>
    /// True when a forced refresh may run now; marks the user so further refreshes within
    /// <see cref="RefreshCooldown"/> are served from cache instead of recomputing.
    /// </summary>
    public bool TryBeginRefresh(Guid userId)
    {
        var key = RefreshKey(userId);
        if (_cache.TryGetValue(key, out _))
            return false;
        _cache.Set(key, true, Entry(RefreshCooldown));
        return true;
    }

    /// <summary>Shared popular list, recomputed every RECOMMENDATION_POPULAR_TTL_MINUTES.</summary>
    public async Task<IReadOnlyList<Guid>> GetOrAddPopularAsync(Func<Task<IReadOnlyList<Guid>>> factory)
    {
        if (_cache.TryGetValue(PopularKey, out IReadOnlyList<Guid>? cached) && cached is not null)
            return cached;
        var popular = await factory();
        _cache.Set(PopularKey, popular, Entry(TimeSpan.FromMinutes(_options.PopularTtlMinutes)));
        return popular;
    }

    public void Dispose() => _cache.Dispose();

    private static MemoryCacheEntryOptions Entry(TimeSpan ttl) =>
        new() { AbsoluteExpirationRelativeToNow = ttl, Size = 1 };

    private static string UserKey(Guid userId) => $"rec:v1:{userId}";

    private static string RefreshKey(Guid userId) => $"rec:refresh:{userId}";
}
