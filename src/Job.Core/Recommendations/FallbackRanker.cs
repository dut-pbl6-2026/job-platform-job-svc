namespace Job.Core.Recommendations;

/// <summary>Why a job is recommended. Serialized as PascalCase string (api-conventions §6).</summary>
public enum RecommendationStrategy
{
    Collaborative,
    Content,
    Popular,
}

/// <summary>Category and locations of the jobs the user interacted with.</summary>
public sealed record ContentProfile(IReadOnlySet<Guid> CategoryIds, IReadOnlySet<string> Locations);

/// <summary>An Active job considered for the content tier.</summary>
public sealed record ContentCandidate(Guid Id, Guid? CategoryId, string Location, int ViewCount, DateTime CreatedAt);

/// <summary>Content-tier match and which attributes matched (drives the reason text).</summary>
public sealed record ContentMatch(Guid JobId, bool SameCategory, bool SameLocation);

/// <summary>An Active job considered for the popular tier.</summary>
public sealed record PopularCandidate(Guid Id, int RecentApplications, int ViewCount, DateTime CreatedAt);

/// <summary>
/// Fallback tiers when collaborative filtering has too little data (PBL6-10). Pure: no I/O.
/// Content: same category or location as the user's jobs. Popular: cold start.
/// </summary>
public static class FallbackRanker
{
    /// <summary>Comparer for locations: trimmed, case-insensitive.</summary>
    public static StringComparer LocationComparer { get; } = StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// Candidates sharing the category or the location of the user's jobs. Matching both comes
    /// first, then ViewCount ↓, CreatedAt ↓, Id.
    /// </summary>
    public static IReadOnlyList<ContentMatch> RankContent(ContentProfile profile, IEnumerable<ContentCandidate> candidates) =>
        candidates
            .Select(c => (
                Candidate: c,
                Match: new ContentMatch(
                    c.Id,
                    SameCategory: c.CategoryId is { } category && profile.CategoryIds.Contains(category),
                    SameLocation: profile.Locations.Contains(c.Location.Trim()))))
            .Where(x => x.Match.SameCategory || x.Match.SameLocation)
            .OrderByDescending(x => x.Match.SameCategory && x.Match.SameLocation)
            .ThenByDescending(x => x.Candidate.ViewCount)
            .ThenByDescending(x => x.Candidate.CreatedAt)
            .ThenBy(x => x.Candidate.Id)
            .Select(x => x.Match)
            .ToList();

    /// <summary>Applications in the recent window ↓, then ViewCount ↓, CreatedAt ↓, Id.</summary>
    public static IReadOnlyList<Guid> RankPopular(IEnumerable<PopularCandidate> candidates) =>
        candidates
            .DistinctBy(c => c.Id)
            .OrderByDescending(c => c.RecentApplications)
            .ThenByDescending(c => c.ViewCount)
            .ThenByDescending(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .Select(c => c.Id)
            .ToList();
}

/// <summary>Vietnamese reason texts (shown by clients). Built only from real job data.</summary>
public static class RecommendationReasons
{
    public const string CollaborativeGeneric = "Ứng viên có lịch sử ứng tuyển giống bạn cũng ứng tuyển việc này";
    public const string Popular = "Đang được nhiều ứng viên quan tâm";

    /// <summary>Anchor title, or the generic text when the anchor job is gone.</summary>
    public static string Collaborative(string? anchorTitle) =>
        string.IsNullOrWhiteSpace(anchorTitle)
            ? CollaborativeGeneric
            : $"Người ứng tuyển \"{anchorTitle}\" cũng ứng tuyển việc này";

    public static string Content(ContentMatch match, string? categoryName, string location) =>
        (match.SameCategory && !string.IsNullOrWhiteSpace(categoryName), match.SameLocation) switch
        {
            (true, true) => $"Cùng ngành {categoryName} tại {location}",
            (true, false) => $"Cùng ngành {categoryName} với việc bạn đã ứng tuyển",
            _ => $"Cùng địa điểm {location} với việc bạn đã ứng tuyển",
        };
}
