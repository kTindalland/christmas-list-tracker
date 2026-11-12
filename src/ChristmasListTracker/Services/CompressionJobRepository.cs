using ChristmasListTracker.Models;
using Dapper;
using Npgsql;

namespace ChristmasListTracker.Services;

public sealed class CompressionJobRepository(NpgsqlDataSource dataSource)
{
    public async Task<CompressionJob?> ClaimNextAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            WITH candidate AS (
                SELECT item_id
                FROM compression_jobs
                WHERE (
                    status IN ('queued', 'processing', 'cleanup_pending', 'abandoned_upload')
                    AND available_at <= now()
                    AND (lease_expires_at IS NULL OR lease_expires_at <= now())
                ) OR (
                    status = 'awaiting_upload'
                    AND upload_expires_at <= now()
                    AND (lease_expires_at IS NULL OR lease_expires_at <= now())
                )
                ORDER BY available_at, created_at
                LIMIT 1
                FOR UPDATE SKIP LOCKED
            )
            UPDATE compression_jobs AS job
            SET status = CASE
                    WHEN job.status = 'queued' OR job.status = 'processing' THEN 'processing'
                    WHEN job.status = 'awaiting_upload' THEN 'abandoned_upload'
                    ELSE job.status
                END,
                attempt_count = CASE
                    WHEN job.status = 'queued' OR job.status = 'processing' THEN job.attempt_count + 1
                    ELSE job.attempt_count
                END,
                lease_expires_at = now() + interval '10 minutes',
                updated_at = now()
            FROM candidate
            WHERE job.item_id = candidate.item_id
            RETURNING job.item_id AS ItemId,
                      job.source_object_id AS SourceObjectId,
                      job.expected_content_type AS ExpectedContentType,
                      job.expected_size AS ExpectedSize,
                      job.status AS Status,
                      job.attempt_count AS AttemptCount,
                      job.compressed_object_id AS CompressedObjectId;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<CompressionJob>(
            new CommandDefinition(sql, cancellationToken: cancellationToken));
    }

    public async Task<bool> PromoteToCompressedAsync(
        CompressionJob job,
        string compressedObjectId,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var jobUpdated = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE compression_jobs
            SET compressed_object_id = @compressedObjectId,
                status = 'cleanup_pending',
                available_at = now() + interval '16 minutes',
                lease_expires_at = NULL,
                attempt_count = 0,
                updated_at = now()
            WHERE item_id = @itemId AND status = 'processing';
            """,
            new { compressedObjectId, itemId = job.ItemId },
            transaction,
            cancellationToken: cancellationToken));
        if (jobUpdated != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE christmas_list_items
            SET object_id = @compressedObjectId,
                image_status = 'ready'
            WHERE id = @itemId;
            """,
            new { compressedObjectId, itemId = job.ItemId },
            transaction,
            cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task MarkCompletedAsync(Guid itemId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE compression_jobs
            SET status = 'completed',
                lease_expires_at = NULL,
                completed_at = now(),
                last_error = NULL,
                updated_at = now()
            WHERE item_id = @itemId AND status = 'cleanup_pending';
            """,
            new { itemId },
            cancellationToken: cancellationToken));
    }

    public async Task RetryProcessingAsync(
        CompressionJob job,
        string error,
        bool permanentFailure,
        CancellationToken cancellationToken)
    {
        var terminal = permanentFailure || job.AttemptCount >= 5;
        var delaySeconds = Math.Min(300, 5 * (1 << Math.Min(job.AttemptCount - 1, 6)));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var updated = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE compression_jobs
            SET status = @status,
                available_at = now() + make_interval(secs => @delaySeconds),
                lease_expires_at = NULL,
                last_error = @error,
                updated_at = now()
            WHERE item_id = @itemId AND status = 'processing';
            """,
            new
            {
                itemId = job.ItemId,
                status = terminal ? "failed" : "queued",
                delaySeconds,
                error = Truncate(error)
            },
            transaction,
            cancellationToken: cancellationToken));
        if (terminal && updated == 1)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE christmas_list_items
                SET image_status = 'failed'
                WHERE id = @itemId;
                """,
                new { itemId = job.ItemId },
                transaction,
                cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RetryCleanupAsync(
        CompressionJob job,
        string error,
        CancellationToken cancellationToken)
    {
        var terminal = job.AttemptCount >= 5;
        var delaySeconds = Math.Min(300, 5 * (1 << Math.Min(Math.Max(1, job.AttemptCount) - 1, 6)));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE compression_jobs
            SET status = @status,
                available_at = now() + make_interval(secs => @delaySeconds),
                lease_expires_at = NULL,
                attempt_count = attempt_count + 1,
                last_error = @error,
                updated_at = now()
            WHERE item_id = @itemId AND status IN ('cleanup_pending', 'abandoned_upload');
            """,
            new
            {
                itemId = job.ItemId,
                status = terminal && job.Status == "cleanup_pending" ? "failed" : job.Status,
                delaySeconds,
                error = Truncate(error)
            },
            cancellationToken: cancellationToken));
    }

    public async Task DeleteAbandonedUploadAsync(Guid itemId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            DELETE FROM christmas_list_items AS item
            WHERE item.id = @itemId
              AND item.image_status = 'uploading'
              AND EXISTS (
                  SELECT 1 FROM compression_jobs AS job
                  WHERE job.item_id = item.id AND job.status = 'abandoned_upload'
              );
            """,
            new { itemId },
            cancellationToken: cancellationToken));
    }

    private static string Truncate(string value) =>
        value.Length <= 2000 ? value : value[..2000];
}
