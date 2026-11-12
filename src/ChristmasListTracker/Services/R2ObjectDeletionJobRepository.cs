using Dapper;
using Npgsql;

namespace ChristmasListTracker.Services;

public sealed class R2ObjectDeletionJobRepository(NpgsqlDataSource dataSource)
{
    public async Task EnqueueAsync(string objectId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO r2_object_deletion_jobs (object_id)
            VALUES (@objectId)
            ON CONFLICT (object_id) DO NOTHING;
            """,
            new { objectId },
            cancellationToken: cancellationToken));
    }

    public async Task<string?> ClaimNextAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            WITH candidate AS (
                SELECT object_id
                FROM r2_object_deletion_jobs
                WHERE available_at <= now()
                  AND (lease_expires_at IS NULL OR lease_expires_at <= now())
                ORDER BY available_at, created_at
                LIMIT 1
                FOR UPDATE SKIP LOCKED
            )
            UPDATE r2_object_deletion_jobs AS job
            SET attempt_count = job.attempt_count + 1,
                lease_expires_at = now() + interval '10 minutes',
                updated_at = now()
            FROM candidate
            WHERE job.object_id = candidate.object_id
            RETURNING job.object_id;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<string>(
            new CommandDefinition(sql, cancellationToken: cancellationToken));
    }

    public async Task MarkDeletedAsync(string objectId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM r2_object_deletion_jobs WHERE object_id = @objectId",
            new { objectId },
            cancellationToken: cancellationToken));
    }

    public async Task RetryAsync(string objectId, string error, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE r2_object_deletion_jobs
            SET available_at = now() + make_interval(secs => @delaySeconds),
                lease_expires_at = NULL,
                last_error = @error,
                updated_at = now()
            WHERE object_id = @objectId;
            """,
            new
            {
                objectId,
                delaySeconds = 60,
                error = error.Length <= 2000 ? error : error[..2000]
            },
            cancellationToken: cancellationToken));
    }
}
