using Job.Core.Recommendations;

namespace Job.Tests.Recommendations;

/// <summary>
/// PBL6-10 slice 4: tier order collaborative → content → popular, no duplicates, nothing the
/// user applied to, Active jobs only. Data enters through POST /api/jobs and the consumer.
/// </summary>
public sealed class RecommendationServiceTests
{
    private static readonly Guid It = RecommendationTestHost.ItCategory;
    private static readonly Guid Finance = RecommendationTestHost.FinanceCategory;

    [Fact]
    public async Task NewUser_GetsPopularOnly()
    {
        await using var host = await RecommendationTestHost.StartAsync();
        var busy = await host.CreateJobAsync("Kế toán", Finance, "Hà Nội");
        var quiet = await host.CreateJobAsync("Lập trình viên .NET", It, "Đà Nẵng");
        await host.Consumer.ApplyAsync(Guid.NewGuid(), busy);

        var result = await host.RecommendAsync(Guid.NewGuid());

        Assert.Equal(new[] { busy, quiet }, result.Items.Select(i => i.Job.Id));
        Assert.All(result.Items, i =>
        {
            Assert.Equal(RecommendationStrategy.Popular, i.Strategy);
            Assert.Equal(RecommendationReasons.Popular, i.Reason);
        });
        Assert.False(result.IsCached);
    }

    [Fact]
    public async Task Popular_FallsBackToViewsWhenNoRecentApplications()
    {
        await using var host = await RecommendationTestHost.StartAsync();
        var fewViews = await host.CreateJobAsync("Kế toán", Finance, "Hà Nội");
        var manyViews = await host.CreateJobAsync("Lập trình viên .NET", It, "Đà Nẵng");
        await host.ViewJobAsync(fewViews, 1);
        await host.ViewJobAsync(manyViews, 3);

        var result = await host.RecommendAsync(Guid.NewGuid());

        Assert.Equal(new[] { manyViews, fewViews }, result.Items.Select(i => i.Job.Id));
    }

    [Fact]
    public async Task HistoryWithoutNeighbours_GetsContentBeforePopular()
    {
        await using var host = await RecommendationTestHost.StartAsync();
        var applied = await host.CreateJobAsync("Lập trình viên Java", It, "Đà Nẵng");
        var both = await host.CreateJobAsync("Lập trình viên .NET", It, "Đà Nẵng");
        var categoryOnly = await host.CreateJobAsync("Kỹ sư dữ liệu", It, "Hà Nội");
        var locationOnly = await host.CreateJobAsync("Kế toán", Finance, "đà nẵng");
        var unrelated = await host.CreateJobAsync("Chuyên viên tài chính", Finance, "Hà Nội");
        var user = Guid.NewGuid();
        await host.Consumer.ApplyAsync(user, applied);
        // Single matches tie-break by ViewCount (then newest): one view puts categoryOnly ahead.
        await host.ViewJobAsync(categoryOnly);

        var result = await host.RecommendAsync(user);

        Assert.Equal(new[] { both, categoryOnly, locationOnly, unrelated }, result.Items.Select(i => i.Job.Id));
        Assert.Equal(
            new[] { RecommendationStrategy.Content, RecommendationStrategy.Content, RecommendationStrategy.Content, RecommendationStrategy.Popular },
            result.Items.Select(i => i.Strategy));
        Assert.Equal("Cùng ngành IT tại Đà Nẵng", result.Items[0].Reason);
        Assert.Equal("Cùng ngành IT với việc bạn đã ứng tuyển", result.Items[1].Reason);
        Assert.Equal("Cùng địa điểm đà nẵng với việc bạn đã ứng tuyển", result.Items[2].Reason);
    }

    [Fact]
    public async Task SharedHistory_PutsCollaborativeFirst_WithAnchorReason()
    {
        await using var host = await RecommendationTestHost.StartAsync();
        var j1 = await host.CreateJobAsync("Lập trình viên Java", It, "Đà Nẵng");
        var j2 = await host.CreateJobAsync("Kế toán tổng hợp", Finance, "Hà Nội");
        var j3 = await host.CreateJobAsync("Lập trình viên .NET", It, "Đà Nẵng");
        var (a, b, c) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await host.Consumer.ApplyAsync(b, j1);
        await host.Consumer.ApplyAsync(b, j2);
        await host.Consumer.ApplyAsync(c, j1);
        await host.Consumer.ApplyAsync(c, j2);
        await host.Consumer.ApplyAsync(a, j1);

        var result = await host.RecommendAsync(a);

        var first = result.Items[0];
        Assert.Equal(j2, first.Job.Id);
        Assert.Equal(RecommendationStrategy.Collaborative, first.Strategy);
        Assert.Equal("Người ứng tuyển \"Lập trình viên Java\" cũng ứng tuyển việc này", first.Reason);
        Assert.Contains(result.Items, i => i.Job.Id == j3 && i.Strategy == RecommendationStrategy.Content);
        Assert.DoesNotContain(result.Items, i => i.Job.Id == j1);
        Assert.Equal(result.Items.Count, result.Items.Select(i => i.Job.Id).Distinct().Count());
    }

    [Fact]
    public async Task SoftDeletedAnchor_UsesGenericReason()
    {
        await using var host = await RecommendationTestHost.StartAsync();
        var j1 = await host.CreateJobAsync("Lập trình viên Java", It, "Đà Nẵng");
        var j2 = await host.CreateJobAsync("Kế toán tổng hợp", Finance, "Hà Nội");
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        await host.Consumer.ApplyAsync(b, j1);
        await host.Consumer.ApplyAsync(b, j2);
        await host.Consumer.ApplyAsync(a, j1);
        await host.DeleteJobAsync(j1);

        var result = await host.RecommendAsync(a);

        var first = result.Items[0];
        Assert.Equal(j2, first.Job.Id);
        Assert.Equal(RecommendationReasons.CollaborativeGeneric, first.Reason);
    }

    [Fact]
    public async Task ClosedPendingAndDeletedJobs_NeverAppear()
    {
        await using var host = await RecommendationTestHost.StartAsync();
        var applied = await host.CreateJobAsync("Lập trình viên Java", It, "Đà Nẵng");
        var closed = await host.CreateJobAsync("Lập trình viên Go", It, "Đà Nẵng");
        var deleted = await host.CreateJobAsync("Lập trình viên Rust", It, "Đà Nẵng");
        var pending = await host.CreatePendingJobAsync("Lập trình viên Kotlin", It, "Đà Nẵng");
        var active = await host.CreateJobAsync("Lập trình viên .NET", It, "Đà Nẵng");
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        foreach (var job in new[] { applied, closed, deleted, pending, active })
            await host.Consumer.ApplyAsync(b, job);
        await host.Consumer.ApplyAsync(a, applied);
        await host.CloseJobAsync(closed);
        await host.DeleteJobAsync(deleted);

        var result = await host.RecommendAsync(a);

        Assert.Equal(new[] { active }, result.Items.Select(i => i.Job.Id));
        Assert.Equal(RecommendationStrategy.Collaborative, result.Items[0].Strategy);
    }

    [Fact]
    public async Task Limit_TruncatesInRankOrder()
    {
        await using var host = await RecommendationTestHost.StartAsync();
        for (var i = 0; i < 12; i++)
            await host.CreateJobAsync($"Việc làm {i}", It, "Đà Nẵng");

        var ten = await host.RecommendAsync(Guid.NewGuid());
        var three = await host.RecommendAsync(Guid.NewGuid(), limit: 3);

        Assert.Equal(10, ten.Items.Count);
        Assert.Equal(ten.Items.Take(3).Select(i => i.Job.Id), three.Items.Select(i => i.Job.Id));
    }

    [Fact]
    public async Task NoActiveJobs_ReturnsEmpty()
    {
        await using var host = await RecommendationTestHost.StartAsync();

        var result = await host.RecommendAsync(Guid.NewGuid());

        Assert.Empty(result.Items);
    }
}
