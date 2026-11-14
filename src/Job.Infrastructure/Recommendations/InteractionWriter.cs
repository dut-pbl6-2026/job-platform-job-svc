using Job.Core.Entities;
using Job.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Job.Infrastructure.Recommendations;

/// <summary>
/// Idempotent writer for the <see cref="JobInteraction"/> read model (PBL6-10). The only
/// write path, used by the Kafka consumer and the startup backfill.
/// </summary>
public sealed class InteractionWriter
{
    private const string InMemoryProvider = "Microsoft.EntityFrameworkCore.InMemory";

    private readonly JobDbContext _db;

    public InteractionWriter(JobDbContext db) => _db = db;

    /// <summary>
    /// Inserts the interaction unless (user, job, type) already exists.
    /// Returns true when a row was inserted, false when it was already there.
    /// </summary>
    public async Task<bool> UpsertAsync(
        Guid userId, Guid jobId, InteractionType type, Guid? sourceId, DateTime occurredAt, CancellationToken ct)
    {
        var row = new JobInteraction(userId, jobId, type, sourceId, occurredAt);

        if (_db.Database.ProviderName == InMemoryProvider)
        {
            // Test-only branch: InMemory has no ON CONFLICT and does not enforce unique indexes.
            var exists = await _db.JobInteractions.AnyAsync(
                x => x.UserId == row.UserId && x.JobId == row.JobId && x.Type == row.Type, ct);
            if (exists)
                return false;
            _db.JobInteractions.Add(row);
            await _db.SaveChangesAsync(ct);
            return true;
        }

        // Postgres: a single atomic statement; the unique index picks the winner under
        // concurrent or redelivered events (no SELECT-then-INSERT race). Parameterized.
        // SourceId is typed explicitly so a null still binds as uuid.
        var inserted = await _db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "JobInteractions" ("Id", "UserId", "JobId", "Type", "SourceId", "OccurredAt", "CreatedAt", "UpdatedAt")
            VALUES (@id, @userId, @jobId, @type, @sourceId, @occurredAt, @createdAt, @updatedAt)
            ON CONFLICT ("UserId", "JobId", "Type") DO NOTHING
            """,
            [
                new NpgsqlParameter("id", row.Id),
                new NpgsqlParameter("userId", row.UserId),
                new NpgsqlParameter("jobId", row.JobId),
                new NpgsqlParameter("type", row.Type.ToString()),
                new NpgsqlParameter("sourceId", NpgsqlDbType.Uuid) { Value = (object?)row.SourceId ?? DBNull.Value },
                new NpgsqlParameter("occurredAt", row.OccurredAt),
                new NpgsqlParameter("createdAt", row.CreatedAt),
                new NpgsqlParameter("updatedAt", row.UpdatedAt),
            ],
            ct);
        return inserted > 0;
    }
}
