using Job.Core.Entities;
using Job.Infrastructure.Workers;

namespace Job.Tests;

/// <summary>Pure decisions of the outbox publisher: backoff after a failure and which rows a cycle may claim.</summary>
public sealed class OutboxPoliciesTests
{
    private static readonly DateTime Now = new(2026, 11, 15, 10, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0, 5)]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(3, 20)]
    [InlineData(6, 160)]
    [InlineData(7, 300)]
    [InlineData(100, 300)]
    public void DelayAfterFailure_DoublesFromBaseAndCapsAtMax(int retryCount, double expectedSeconds) =>
        Assert.Equal(expectedSeconds, OutboxRetryPolicy.DelayAfterFailure(retryCount).TotalSeconds);

    [Fact]
    public void DelayAfterFailure_NeverDecreases_AndNeverGivesUp()
    {
        var previous = TimeSpan.Zero;
        for (var retry = 1; retry <= 1_000; retry++)
        {
            var delay = OutboxRetryPolicy.DelayAfterFailure(retry);
            Assert.True(delay >= previous);
            Assert.True(delay <= OutboxRetryPolicy.MaxDelay);
            previous = delay;
        }

        Assert.Equal(OutboxRetryPolicy.MaxDelay, previous);
    }

    [Fact]
    public void SelectClaimable_TakesDueRowsInOrder_UpToBatchSize()
    {
        var rows = Enumerable.Range(0, 5).Select(i => Row($"job-{i}")).ToList();

        var selected = OutboxClaimPolicy.SelectClaimable(rows, Now, batchSize: 3);

        Assert.Equal(rows.Take(3), selected);
    }

    [Fact]
    public void SelectClaimable_SkipsRowsNotYetDue()
    {
        var notDue = Row("job-1");
        notDue.RecordFailure(Now.AddSeconds(30));
        var due = Row("job-2");

        var selected = OutboxClaimPolicy.SelectClaimable([notDue, due], Now, batchSize: 10);

        Assert.Equal([due], selected);
    }

    [Fact]
    public void SelectClaimable_LaterRowOfSameJob_WaitsForEarlierUnpublishedRow()
    {
        // The earlier row is in backoff, so the later row of the same job must not overtake it.
        var earlier = Row("job-1");
        earlier.RecordFailure(Now.AddSeconds(30));
        var later = Row("job-1");

        var selected = OutboxClaimPolicy.SelectClaimable([earlier, later], Now, batchSize: 10);

        Assert.Empty(selected);
    }

    [Fact]
    public void SelectClaimable_DueHeadOfJob_ClaimsOnlyWhatComesFirst()
    {
        var first = Row("job-1");
        var second = Row("job-1");

        var selected = OutboxClaimPolicy.SelectClaimable([first, second], Now, batchSize: 10);

        Assert.Equal([first], selected);
    }

    [Fact]
    public void SelectClaimable_DifferentJobs_DoNotBlockEachOther()
    {
        var a = Row("job-a");
        a.RecordFailure(Now.AddMinutes(1));
        var b = Row("job-b");

        var selected = OutboxClaimPolicy.SelectClaimable([a, b], Now, batchSize: 10);

        Assert.Equal([b], selected);
    }

    [Fact]
    public void SelectClaimable_RowWithoutAggregate_IsNeverBlocked()
    {
        var first = new OutboxEvent("job.updated", "{}", aggregateId: null);
        var second = new OutboxEvent("job.updated", "{}", aggregateId: null);

        var selected = OutboxClaimPolicy.SelectClaimable([first, second], Now, batchSize: 10);

        Assert.Equal([first, second], selected);
    }

    private static OutboxEvent Row(string aggregateId) =>
        new("job.updated", "{}", aggregateId);
}
