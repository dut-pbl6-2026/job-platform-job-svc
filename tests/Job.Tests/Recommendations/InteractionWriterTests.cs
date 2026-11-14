using Job.Core.Entities;
using Job.Infrastructure.Data;
using Job.Infrastructure.Recommendations;
using Microsoft.EntityFrameworkCore;

namespace Job.Tests.Recommendations;

/// <summary>
/// PBL6-10 slice 1: the JobInteractions read model is written idempotently — the same
/// (user, job, type) delivered twice (Kafka at-least-once, backfill overlap) stays one row.
/// Postgres uses INSERT … ON CONFLICT DO NOTHING; this suite covers the InMemory branch.
/// </summary>
public sealed class InteractionWriterTests : IDisposable
{
    private readonly JobDbContext _db;

    public InteractionWriterTests()
    {
        _db = new JobDbContext(new DbContextOptionsBuilder<JobDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Upsert_SameInteractionTwice_StoresOneRow()
    {
        var writer = new InteractionWriter(_db);
        var userId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        var first = await writer.UpsertAsync(
            userId, jobId, InteractionType.Applied, Guid.NewGuid(), DateTime.UtcNow, CancellationToken.None);
        var second = await writer.UpsertAsync(
            userId, jobId, InteractionType.Applied, Guid.NewGuid(), DateTime.UtcNow, CancellationToken.None);

        Assert.True(first);
        Assert.False(second);
        var row = Assert.Single(await _db.JobInteractions.ToListAsync());
        Assert.Equal(userId, row.UserId);
        Assert.Equal(jobId, row.JobId);
        Assert.Equal(InteractionType.Applied, row.Type);
    }

    [Fact]
    public async Task Upsert_DifferentJobs_StoresOneRowEach()
    {
        var writer = new InteractionWriter(_db);
        var userId = Guid.NewGuid();

        await writer.UpsertAsync(userId, Guid.NewGuid(), InteractionType.Applied, null, DateTime.UtcNow, CancellationToken.None);
        await writer.UpsertAsync(userId, Guid.NewGuid(), InteractionType.Applied, null, DateTime.UtcNow, CancellationToken.None);

        Assert.Equal(2, await _db.JobInteractions.CountAsync());
    }

    [Fact]
    public void Interaction_RejectsEmptyIds()
    {
        Assert.Throws<ArgumentException>(() =>
            new JobInteraction(Guid.Empty, Guid.NewGuid(), InteractionType.Applied, null, DateTime.UtcNow));
        Assert.Throws<ArgumentException>(() =>
            new JobInteraction(Guid.NewGuid(), Guid.Empty, InteractionType.Applied, null, DateTime.UtcNow));
    }

    [Fact]
    public void Interaction_StoresOccurredAtAsUtc()
    {
        var unspecified = new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Unspecified);

        var interaction = new JobInteraction(
            Guid.NewGuid(), Guid.NewGuid(), InteractionType.Applied, null, unspecified);

        Assert.Equal(DateTimeKind.Utc, interaction.OccurredAt.Kind);
        Assert.Equal(unspecified.Ticks, interaction.OccurredAt.Ticks);
    }
}
