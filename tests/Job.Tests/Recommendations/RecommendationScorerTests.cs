using Job.Core.Recommendations;

namespace Job.Tests.Recommendations;

/// <summary>
/// PBL6-10 slice 2: item-item collaborative filtering.
/// sim(i, j) = co(i, j) / sqrt(n(i) · n(j)); score(j) = Σ w_i · sim(i, j) over the user's jobs.
/// </summary>
public sealed class RecommendationScorerTests
{
    private static readonly DateTime Now = new(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc);

    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();
    private static readonly Guid C = Guid.NewGuid();

    [Fact]
    public void Score_RanksJobsByNormalizedCooccurrence()
    {
        // Two neighbours applied to A and B, one to A and C.
        var v1 = Guid.NewGuid();
        var v2 = Guid.NewGuid();
        var v3 = Guid.NewGuid();
        var neighbours = new[]
        {
            new NeighborInteraction(v1, A), new NeighborInteraction(v1, B),
            new NeighborInteraction(v2, A), new NeighborInteraction(v2, B),
            new NeighborInteraction(v3, A), new NeighborInteraction(v3, C),
        };
        var counts = new Dictionary<Guid, int> { [A] = 4, [B] = 2, [C] = 1 };

        var result = RecommendationScorer.ScoreCollaborative(
            Weights((A, 1.0)), neighbours, counts, Candidates(B, C));

        Assert.Equal(new[] { B, C }, result.Select(r => r.JobId));
        Assert.Equal(2 / Math.Sqrt(4 * 2), result[0].Score, 6);
        Assert.Equal(1 / Math.Sqrt(4 * 1), result[1].Score, 6);
    }

    [Fact]
    public void Score_VeryPopularJobDoesNotDominateNicheMatch()
    {
        var popular = Guid.NewGuid();
        var niche = Guid.NewGuid();
        var v1 = Guid.NewGuid();
        var v2 = Guid.NewGuid();
        // Both co-occur twice with A, but the popular job has 100 applicants overall.
        var neighbours = new[]
        {
            new NeighborInteraction(v1, A), new NeighborInteraction(v1, popular), new NeighborInteraction(v1, niche),
            new NeighborInteraction(v2, A), new NeighborInteraction(v2, popular), new NeighborInteraction(v2, niche),
        };
        var counts = new Dictionary<Guid, int> { [A] = 3, [popular] = 100, [niche] = 2 };
        var candidates = new Dictionary<Guid, CandidateJob>
        {
            [popular] = new(popular, ViewCount: 10_000, CreatedAt: Now),
            [niche] = new(niche, ViewCount: 1, CreatedAt: Now),
        };

        var result = RecommendationScorer.ScoreCollaborative(Weights((A, 1.0)), neighbours, counts, candidates);

        Assert.Equal(niche, result[0].JobId);
        Assert.Equal(popular, result[1].JobId);
    }

    [Fact]
    public void Score_ExcludesJobsTheUserAlreadyInteractedWith()
    {
        var v = Guid.NewGuid();
        var neighbours = new[]
        {
            new NeighborInteraction(v, A), new NeighborInteraction(v, B), new NeighborInteraction(v, C),
        };
        var counts = new Dictionary<Guid, int> { [A] = 2, [B] = 2, [C] = 1 };

        var result = RecommendationScorer.ScoreCollaborative(
            Weights((A, 1.0), (B, 1.0)), neighbours, counts, Candidates(B, C));

        Assert.Equal(new[] { C }, result.Select(r => r.JobId));
    }

    [Fact]
    public void Score_IgnoresJobsThatAreNotCandidates()
    {
        // C is not Active, so it is not in the candidate set.
        var v = Guid.NewGuid();
        var neighbours = new[] { new NeighborInteraction(v, A), new NeighborInteraction(v, C) };
        var counts = new Dictionary<Guid, int> { [A] = 2, [C] = 1 };

        var result = RecommendationScorer.ScoreCollaborative(
            Weights((A, 1.0)), neighbours, counts, Candidates(B));

        Assert.Empty(result);
    }

    [Fact]
    public void Score_AnchorIsTheUserJobContributingMost()
    {
        var saved = Guid.NewGuid();
        var x = Guid.NewGuid();
        var v1 = Guid.NewGuid();
        var v2 = Guid.NewGuid();
        // X co-occurs once with A (applied, weight 1.0) and once with a saved job (weight 0.5).
        var neighbours = new[]
        {
            new NeighborInteraction(v1, saved), new NeighborInteraction(v1, x),
            new NeighborInteraction(v2, A), new NeighborInteraction(v2, x),
        };
        var counts = new Dictionary<Guid, int> { [A] = 2, [saved] = 2, [x] = 2 };

        var result = RecommendationScorer.ScoreCollaborative(
            Weights((saved, 0.5), (A, 1.0)), neighbours, counts, Candidates(x));

        var match = Assert.Single(result);
        Assert.Equal(A, match.AnchorJobId);
        Assert.Equal((1.0 + 0.5) * (1 / Math.Sqrt(2 * 2)), match.Score, 6);
    }

    [Fact]
    public void Score_TiesBreakByViewCountThenCreatedAtThenId()
    {
        var d = Guid.NewGuid();
        var v = Guid.NewGuid();
        var neighbours = new[]
        {
            new NeighborInteraction(v, A), new NeighborInteraction(v, B),
            new NeighborInteraction(v, C), new NeighborInteraction(v, d),
        };
        var counts = new Dictionary<Guid, int> { [A] = 2, [B] = 1, [C] = 1, [d] = 1 };
        var candidates = new Dictionary<Guid, CandidateJob>
        {
            [B] = new(B, ViewCount: 5, CreatedAt: Now),
            [C] = new(C, ViewCount: 10, CreatedAt: Now.AddDays(-3)),
            [d] = new(d, ViewCount: 5, CreatedAt: Now.AddDays(1)),
        };

        var result = RecommendationScorer.ScoreCollaborative(Weights((A, 1.0)), neighbours, counts, candidates);

        Assert.Equal(new[] { C, d, B }, result.Select(r => r.JobId));
    }

    [Fact]
    public void Score_RespectsMinimumCooccurrence()
    {
        var v1 = Guid.NewGuid();
        var v2 = Guid.NewGuid();
        var neighbours = new[]
        {
            new NeighborInteraction(v1, A), new NeighborInteraction(v1, B),
            new NeighborInteraction(v2, A), new NeighborInteraction(v2, B),
            new NeighborInteraction(v1, C),
        };
        var counts = new Dictionary<Guid, int> { [A] = 3, [B] = 2, [C] = 1 };

        var result = RecommendationScorer.ScoreCollaborative(
            Weights((A, 1.0)), neighbours, counts, Candidates(B, C), minCooccurrence: 2);

        Assert.Equal(new[] { B }, result.Select(r => r.JobId));
    }

    [Fact]
    public void Score_EmptyInputs_ReturnEmpty()
    {
        Assert.Empty(RecommendationScorer.ScoreCollaborative(
            new Dictionary<Guid, double>(), [], new Dictionary<Guid, int>(), new Dictionary<Guid, CandidateJob>()));
        Assert.Empty(RecommendationScorer.ScoreCollaborative(
            Weights((A, 1.0)), [], new Dictionary<Guid, int> { [A] = 1 }, Candidates(B)));
    }

    private static Dictionary<Guid, double> Weights(params (Guid Job, double Weight)[] items) =>
        items.ToDictionary(i => i.Job, i => i.Weight);

    private static Dictionary<Guid, CandidateJob> Candidates(params Guid[] ids) =>
        ids.ToDictionary(id => id, id => new CandidateJob(id, ViewCount: 0, CreatedAt: Now));
}
