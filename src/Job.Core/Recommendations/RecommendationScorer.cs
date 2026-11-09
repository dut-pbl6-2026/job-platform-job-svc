namespace Job.Core.Recommendations;

/// <summary>A job that may be recommended (Active), with the fields used to break ties.</summary>
public sealed record CandidateJob(Guid Id, int ViewCount, DateTime CreatedAt);

/// <summary>One (user, job) pair from another user who shares history with the target user.</summary>
public sealed record NeighborInteraction(Guid UserId, Guid JobId);

/// <summary>
/// Collaborative result: <see cref="AnchorJobId"/> is the user's job that contributed most,
/// used to explain the recommendation.
/// </summary>
public sealed record CollaborativeMatch(Guid JobId, double Score, Guid AnchorJobId);

/// <summary>
/// Item-item collaborative filtering on a binary user × job matrix (PBL6-10). Pure: no I/O.
/// <code>
/// sim(i, j)  = co(i, j) / sqrt(n(i) · n(j))    co = users who interacted with both, n = users per job
/// score(j)   = Σ_{i ∈ I_u} w_i · sim(i, j)      for j ∉ I_u, j a candidate
/// anchor(j)  = argmax_i w_i · sim(i, j)
/// </code>
/// Cosine normalisation keeps a job everyone applies to from outranking a closer match.
/// </summary>
public static class RecommendationScorer
{
    /// <param name="userJobWeights">I_u: the target user's jobs and their weights (applied 1.0, saved 0.5).</param>
    /// <param name="neighborInteractions">All interactions of neighbour users (users sharing a job with the target).</param>
    /// <param name="jobUserCounts">n(j): number of distinct users who interacted with each job.</param>
    /// <param name="candidates">Jobs that may be recommended (Active). Others are ignored.</param>
    /// <param name="minCooccurrence">Pairs with fewer common users are ignored.</param>
    /// <returns>Matches with score &gt; 0, best first; ties by ViewCount ↓, CreatedAt ↓, Id.</returns>
    public static IReadOnlyList<CollaborativeMatch> ScoreCollaborative(
        IReadOnlyDictionary<Guid, double> userJobWeights,
        IEnumerable<NeighborInteraction> neighborInteractions,
        IReadOnlyDictionary<Guid, int> jobUserCounts,
        IReadOnlyDictionary<Guid, CandidateJob> candidates,
        int minCooccurrence = 1)
    {
        if (userJobWeights.Count == 0 || candidates.Count == 0)
            return [];

        // co(i, j) for i ∈ I_u and j a candidate outside I_u.
        var cooccurrence = new Dictionary<(Guid Anchor, Guid Job), int>();
        foreach (var neighbourJobs in neighborInteractions
                     .GroupBy(x => x.UserId)
                     .Select(g => g.Select(x => x.JobId).Distinct().ToList()))
        {
            var shared = neighbourJobs.Where(userJobWeights.ContainsKey).ToList();
            if (shared.Count == 0)
                continue;
            foreach (var job in neighbourJobs)
            {
                if (userJobWeights.ContainsKey(job) || !candidates.ContainsKey(job))
                    continue;
                foreach (var anchor in shared)
                    cooccurrence[(anchor, job)] = cooccurrence.GetValueOrDefault((anchor, job)) + 1;
            }
        }

        var scores = new Dictionary<Guid, (double Score, Guid Anchor, double Best)>();
        foreach (var ((anchor, job), co) in cooccurrence)
        {
            if (co < Math.Max(1, minCooccurrence))
                continue;
            var nAnchor = jobUserCounts.GetValueOrDefault(anchor);
            var nJob = jobUserCounts.GetValueOrDefault(job);
            if (nAnchor <= 0 || nJob <= 0)
                continue;

            var contribution = userJobWeights[anchor] * co / Math.Sqrt((double)nAnchor * nJob);
            if (!scores.TryGetValue(job, out var current))
            {
                scores[job] = (contribution, anchor, contribution);
                continue;
            }

            // Deterministic anchor on equal contribution: lowest id wins.
            var better = contribution > current.Best
                         || (contribution == current.Best && anchor.CompareTo(current.Anchor) < 0);
            scores[job] = (
                current.Score + contribution,
                better ? anchor : current.Anchor,
                better ? contribution : current.Best);
        }

        return scores
            .Where(s => s.Value.Score > 0)
            .Select(s => (Match: new CollaborativeMatch(s.Key, s.Value.Score, s.Value.Anchor), Job: candidates[s.Key]))
            .OrderByDescending(x => x.Match.Score)
            .ThenByDescending(x => x.Job.ViewCount)
            .ThenByDescending(x => x.Job.CreatedAt)
            .ThenBy(x => x.Job.Id)
            .Select(x => x.Match)
            .ToList();
    }
}
