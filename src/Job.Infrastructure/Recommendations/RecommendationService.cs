using Job.Core.Entities;
using Job.Core.Recommendations;
using Job.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using JobPosting = Job.Core.Entities.Job;

namespace Job.Infrastructure.Recommendations;

/// <summary>One ranked recommendation, before job details are loaded.</summary>
public sealed record RecommendationEntry(Guid JobId, RecommendationStrategy Strategy, string Reason);

/// <summary>A computed ranking (top <see cref="RecommendationService.MaxItems"/>) and when it was computed.</summary>
public sealed record RecommendationSnapshot(IReadOnlyList<RecommendationEntry> Entries, DateTime GeneratedAt);

/// <summary>A recommendation with the job loaded (Company and Category included).</summary>
public sealed record RecommendationItem(JobPosting Job, RecommendationStrategy Strategy, string Reason);

/// <summary>Result returned to the endpoint.</summary>
public sealed record RecommendationResult(IReadOnlyList<RecommendationItem> Items, bool IsCached, DateTime GeneratedAt);

/// <summary>
/// Personal job recommendations (PBL6-10). Three tiers, filled in order up to the limit, no
/// duplicates, never a job the user already applied to or saved, Active jobs only:
/// collaborative (item-item CF) → content (same category/location) → popular (cold start).
/// </summary>
public sealed class RecommendationService
{
    public const int MaxItems = 10;
    public const int HistoryLimit = 50;
    public const double AppliedWeight = 1.0;
    public const double SavedWeight = 0.5;
    public const int PopularWindowDays = 30;

    private const int ContentPoolSize = 500;
    private const int PopularPoolSize = 100;

    private readonly JobDbContext _db;
    private readonly RecommendationOptions _options;
    private readonly TimeProvider _time;

    public RecommendationService(JobDbContext db, IOptions<RecommendationOptions> options, TimeProvider time)
    {
        _db = db;
        _options = options.Value;
        _time = time;
    }

    /// <summary>Computes the ranking and loads the jobs.</summary>
    public async Task<RecommendationResult> GetAsync(Guid userId, int limit, CancellationToken ct)
    {
        var snapshot = await ComputeAsync(userId, ct);
        return new RecommendationResult(await HydrateAsync(snapshot.Entries, limit, ct), IsCached: false, snapshot.GeneratedAt);
    }

    /// <summary>Ranks up to <see cref="MaxItems"/> jobs for the user.</summary>
    public async Task<RecommendationSnapshot> ComputeAsync(Guid userId, CancellationToken ct)
    {
        var generatedAt = _time.GetUtcNow().UtcDateTime;
        var entries = new List<RecommendationEntry>(MaxItems);
        var chosen = new HashSet<Guid>();

        var applied = await _db.JobInteractions.AsNoTracking()
            .Where(x => x.UserId == userId && x.Type == InteractionType.Applied)
            .OrderByDescending(x => x.OccurredAt)
            .Select(x => x.JobId)
            .ToListAsync(ct);
        var saved = await _db.SavedJobs.AsNoTracking()
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => x.JobId)
            .ToListAsync(ct);

        // Never recommend what the user already applied to or saved.
        var excluded = applied.Concat(saved).ToHashSet();

        // I_u: most recent interactions, applied outweighs saved.
        var weights = new Dictionary<Guid, double>();
        foreach (var jobId in saved.Take(HistoryLimit))
            weights[jobId] = SavedWeight;
        foreach (var jobId in applied.Take(HistoryLimit))
            weights[jobId] = AppliedWeight;

        if (weights.Count > 0)
        {
            await AddCollaborativeAsync(userId, weights, excluded, entries, chosen, ct);
            if (entries.Count < MaxItems)
                await AddContentAsync(weights.Keys.ToList(), excluded, entries, chosen, ct);
        }

        if (entries.Count < MaxItems)
            await AddPopularAsync(excluded, entries, chosen, ct);

        return new RecommendationSnapshot(entries, generatedAt);
    }

    /// <summary>Loads the entries' jobs, keeps rank order, drops jobs that are no longer Active.</summary>
    public async Task<IReadOnlyList<RecommendationItem>> HydrateAsync(
        IReadOnlyList<RecommendationEntry> entries, int limit, CancellationToken ct)
    {
        var ids = entries.Select(e => e.JobId).ToList();
        var jobs = await _db.Jobs.AsNoTracking()
            .Include(j => j.Company)
            .Include(j => j.Category)
            .Where(j => ids.Contains(j.Id) && j.Status == JobStatus.Active)
            .ToDictionaryAsync(j => j.Id, ct);

        return entries
            .Where(e => jobs.ContainsKey(e.JobId))
            .Take(limit)
            .Select(e => new RecommendationItem(jobs[e.JobId], e.Strategy, e.Reason))
            .ToList();
    }

    /// <summary>Popular Active job ids, best first (shared by every user; cached by the caller).</summary>
    public async Task<IReadOnlyList<Guid>> ComputePopularAsync(CancellationToken ct)
    {
        var since = _time.GetUtcNow().UtcDateTime.AddDays(-PopularWindowDays);
        var recent = await _db.JobInteractions.AsNoTracking()
            .Where(x => x.Type == InteractionType.Applied && x.OccurredAt >= since)
            .GroupBy(x => x.JobId)
            .Select(g => new { JobId = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(PopularPoolSize)
            .ToDictionaryAsync(x => x.JobId, x => x.Count, ct);
        var recentIds = recent.Keys.ToList();

        var withApplications = await ActiveJobs()
            .Where(j => recentIds.Contains(j.Id))
            .Select(j => new { j.Id, j.ViewCount, j.CreatedAt })
            .ToListAsync(ct);
        var mostViewed = await ActiveJobs()
            .OrderByDescending(j => j.ViewCount)
            .ThenByDescending(j => j.CreatedAt)
            .Take(PopularPoolSize)
            .Select(j => new { j.Id, j.ViewCount, j.CreatedAt })
            .ToListAsync(ct);

        return FallbackRanker.RankPopular(withApplications.Concat(mostViewed)
                .Select(j => new PopularCandidate(j.Id, recent.GetValueOrDefault(j.Id), j.ViewCount, j.CreatedAt)))
            .Take(PopularPoolSize)
            .ToList();
    }

    private IQueryable<JobPosting> ActiveJobs() =>
        _db.Jobs.AsNoTracking().Where(j => j.Status == JobStatus.Active);

    private async Task AddCollaborativeAsync(
        Guid userId,
        Dictionary<Guid, double> weights,
        HashSet<Guid> excluded,
        List<RecommendationEntry> entries,
        HashSet<Guid> chosen,
        CancellationToken ct)
    {
        var userJobs = weights.Keys.ToList();

        // Neighbours: other users sharing a job with the user, most recent first.
        var neighbours = await _db.JobInteractions.AsNoTracking()
            .Where(x => userJobs.Contains(x.JobId) && x.UserId != userId)
            .GroupBy(x => x.UserId)
            .Select(g => new { UserId = g.Key, Last = g.Max(x => x.OccurredAt) })
            .OrderByDescending(x => x.Last)
            .ThenBy(x => x.UserId)
            .Take(_options.MaxNeighbors)
            .Select(x => x.UserId)
            .ToListAsync(ct);
        if (neighbours.Count == 0)
            return;

        var neighbourInteractions = await _db.JobInteractions.AsNoTracking()
            .Where(x => neighbours.Contains(x.UserId))
            .Select(x => new NeighborInteraction(x.UserId, x.JobId))
            .ToListAsync(ct);

        var candidateIds = neighbourInteractions
            .Select(x => x.JobId)
            .Where(id => !excluded.Contains(id))
            .Distinct()
            .ToList();
        var candidates = await ActiveJobs()
            .Where(j => candidateIds.Contains(j.Id))
            .Select(j => new CandidateJob(j.Id, j.ViewCount, j.CreatedAt))
            .ToDictionaryAsync(j => j.Id, ct);
        if (candidates.Count == 0)
            return;

        // n(j) over every user (one Applied row per user and job).
        var countedJobs = candidates.Keys.Concat(userJobs).ToList();
        var counts = await _db.JobInteractions.AsNoTracking()
            .Where(x => countedJobs.Contains(x.JobId))
            .GroupBy(x => x.JobId)
            .Select(g => new { JobId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.JobId, x => x.Count, ct);

        var matches = RecommendationScorer
            .ScoreCollaborative(weights, neighbourInteractions, counts, candidates, _options.MinCooccurrence)
            .Take(MaxItems - entries.Count)
            .ToList();
        if (matches.Count == 0)
            return;

        // Anchor titles; a soft-deleted anchor falls back to the generic reason.
        var anchorIds = matches.Select(m => m.AnchorJobId).Distinct().ToList();
        var anchorTitles = await _db.Jobs.AsNoTracking().IgnoreQueryFilters()
            .Where(j => anchorIds.Contains(j.Id) && j.Status != JobStatus.Deleted)
            .ToDictionaryAsync(j => j.Id, j => j.Title, ct);

        foreach (var match in matches)
        {
            if (chosen.Add(match.JobId))
            {
                entries.Add(new RecommendationEntry(
                    match.JobId,
                    RecommendationStrategy.Collaborative,
                    RecommendationReasons.Collaborative(anchorTitles.GetValueOrDefault(match.AnchorJobId))));
            }
        }
    }

    private async Task AddContentAsync(
        List<Guid> userJobs,
        HashSet<Guid> excluded,
        List<RecommendationEntry> entries,
        HashSet<Guid> chosen,
        CancellationToken ct)
    {
        // The user's taste includes jobs that have since closed.
        var history = await _db.Jobs.AsNoTracking().IgnoreQueryFilters()
            .Where(j => userJobs.Contains(j.Id))
            .Select(j => new { j.CategoryId, j.Location })
            .ToListAsync(ct);
        var categories = history.Where(h => h.CategoryId.HasValue).Select(h => h.CategoryId!.Value).ToHashSet();
        var locations = history
            .Select(h => h.Location.Trim())
            .Where(l => l.Length > 0)
            .ToHashSet(FallbackRanker.LocationComparer);
        if (categories.Count == 0 && locations.Count == 0)
            return;

        var lowerLocations = locations.Select(l => l.ToLowerInvariant()).ToList();
        var skip = excluded.Concat(chosen).ToList();
        var pool = await ActiveJobs()
            .Where(j => !skip.Contains(j.Id)
                        && ((j.CategoryId.HasValue && categories.Contains(j.CategoryId.Value))
                            || lowerLocations.Contains(j.Location.ToLower())))
            .OrderByDescending(j => j.ViewCount)
            .ThenByDescending(j => j.CreatedAt)
            .Take(ContentPoolSize)
            .Select(j => new
            {
                j.Id,
                j.CategoryId,
                CategoryName = j.Category != null ? j.Category.Name : null,
                j.Location,
                j.ViewCount,
                j.CreatedAt,
            })
            .ToListAsync(ct);
        var byId = pool.ToDictionary(j => j.Id);

        var matches = FallbackRanker.RankContent(
            new ContentProfile(categories, locations),
            pool.Select(j => new ContentCandidate(j.Id, j.CategoryId, j.Location, j.ViewCount, j.CreatedAt)));

        foreach (var match in matches)
        {
            if (entries.Count >= MaxItems)
                break;
            if (!chosen.Add(match.JobId))
                continue;
            var job = byId[match.JobId];
            entries.Add(new RecommendationEntry(
                match.JobId,
                RecommendationStrategy.Content,
                RecommendationReasons.Content(match, job.CategoryName, job.Location)));
        }
    }

    private async Task AddPopularAsync(
        HashSet<Guid> excluded,
        List<RecommendationEntry> entries,
        HashSet<Guid> chosen,
        CancellationToken ct)
    {
        var popular = await ComputePopularAsync(ct);
        foreach (var jobId in popular)
        {
            if (entries.Count >= MaxItems)
                break;
            if (excluded.Contains(jobId) || !chosen.Add(jobId))
                continue;
            entries.Add(new RecommendationEntry(jobId, RecommendationStrategy.Popular, RecommendationReasons.Popular));
        }
    }
}
