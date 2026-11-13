using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using System.Text.Json;

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

    private readonly IDatabase? _redis;

    private readonly ILogger<RecommendationCache> _logger;

    public RecommendationCache(
        IOptions<RecommendationOptions> options,
        ILogger<RecommendationCache> logger,
        IConnectionMultiplexer? connection = null)
    {
        _options = options.Value;
        _logger = logger;
        _redis = connection?.GetDatabase();
    }

    public async Task<RecommendationSnapshot?> GetAsync(Guid userId, CancellationToken ct)
    {
        var key = UserKey(userId);

        if (_redis is null)
            return _cache.TryGetValue<RecommendationSnapshot>(key, out var cached) ? cached : null;

        try
        {
            var value = await _redis.StringGetAsync(key).WaitAsync(TimeSpan.FromMilliseconds(50), ct);
            return value.IsNull ? null : JsonSerializer.Deserialize<RecommendationSnapshot>(value.ToString());
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException or JsonException)
        {
            _logger.LogWarning(ex, "Could not read recommendation cache. Treating as a cache miss.");
            return null;
        }
    }

    public async Task SetAsync(
      Guid userId, RecommendationSnapshot snapshot, CancellationToken ct)
    {
        var key = UserKey(userId);
        var ttl = TimeSpan.FromHours(_options.CacheTtlHours);

        if (_redis is null)
        {
            _cache.Set(key, snapshot, Entry(ttl));
            return;
        }

        try
        {
            await _redis.StringSetAsync(key, JsonSerializer.Serialize(snapshot), ttl)
              .WaitAsync(TimeSpan.FromMilliseconds(50), ct);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            _logger.LogWarning(ex, "Could not write recommendation cache.");
        }
    }

    public async Task InvalidateAsync(Guid userId, CancellationToken ct)
    {
        var key = UserKey(userId);

        if (_redis is null)
        {
            _cache.Remove(key);
            return;
        }

        await _redis.KeyDeleteAsync(key).WaitAsync(ct);
    }

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

    private static string UserKey(Guid userId) => $"recommend:{userId:D}";
    private static string RefreshKey(Guid userId) => $"rec:refresh:{userId}";
}
