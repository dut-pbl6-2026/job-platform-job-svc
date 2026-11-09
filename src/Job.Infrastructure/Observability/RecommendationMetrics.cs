using System.Diagnostics.Metrics;
using Job.Core.Recommendations;

namespace Job.Infrastructure.Observability;

/// <summary>
/// Recommendation metrics (PBL6-10), Meter <c>job.recommendations</c>, exported on /metrics.
/// No user ids in tags (unbounded cardinality, and they identify people).
/// </summary>
public static class RecommendationMetrics
{
    public const string MeterName = "job.recommendations";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> Served = Meter.CreateCounter<long>(
        "job.recommendations.served", unit: "{item}", description: "Recommended jobs returned, by strategy.");

    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(
        "job.recommendations.duration", unit: "ms", description: "Time to answer a recommendation request.");

    private static readonly Counter<long> Cache = Meter.CreateCounter<long>(
        "job.recommendations.cache", unit: "{request}", description: "Per-user cache lookups (result=hit|miss).");

    private static readonly Counter<long> Backfill = Meter.CreateCounter<long>(
        "job.recommendations.backfill.rows", unit: "{row}", description: "Interactions inserted by the startup backfill.");

    public static void CacheHit() => Cache.Add(1, new KeyValuePair<string, object?>("result", "hit"));

    public static void CacheMiss() => Cache.Add(1, new KeyValuePair<string, object?>("result", "miss"));

    public static void ServedItem(RecommendationStrategy strategy) =>
        Served.Add(1, new KeyValuePair<string, object?>("strategy", strategy.ToString()));

    public static void RequestDuration(TimeSpan elapsed, bool cached) =>
        Duration.Record(elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("cached", cached));

    public static void BackfillRows(int inserted) => Backfill.Add(inserted);
}
