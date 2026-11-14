namespace Job.Api.DTOs;

/// <summary>
/// Response of <c>GET /api/jobs/recommended</c>. Bounded top-N (≤ 10), so no paging envelope:
/// <c>Total</c> equals <c>Items.Count</c>. <c>IsCached</c> is true when served from the per-user
/// cache; <c>GeneratedAt</c> is when the ranking was computed (UTC).
/// </summary>
public record RecommendationResponse(
    IReadOnlyList<RecommendedJobDto> Items,
    int Total,
    bool IsCached,
    DateTime GeneratedAt);
