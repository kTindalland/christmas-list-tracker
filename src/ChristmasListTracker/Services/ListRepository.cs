using ChristmasListTracker.Models;
using Dapper;
using Npgsql;

namespace ChristmasListTracker.Services;

public sealed class ListRepository(NpgsqlDataSource dataSource)
{
    public async Task<IReadOnlyList<FamilyUser>> GetUsersAsync(CancellationToken cancellationToken = default)
    {
        return await ApplicationTelemetry.TrackAsync("db.users.select", async () =>
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            var users = await connection.QueryAsync<FamilyUser>(
                new CommandDefinition(
                    "SELECT id, display_name AS DisplayName FROM users ORDER BY display_name",
                    cancellationToken: cancellationToken));
            return (IReadOnlyList<FamilyUser>)users.AsList();
        });
    }

    public async Task<FamilyUser?> GetUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<FamilyUser>(
            new CommandDefinition(
                "SELECT id, display_name AS DisplayName FROM users WHERE id = @userId",
                new { userId },
                cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<ChristmasListItemRow>> GetListAsync(
        Guid ownerUserId,
        Guid viewerUserId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT item.id,
                   item.user_id AS OwnerUserId,
                   item.object_id AS ObjectId,
                   item.created_at AS CreatedAt,
                   item.image_status AS ImageStatus,
                   CASE WHEN item.user_id <> @viewerUserId
                        THEN item.purchased_by_user_id IS NOT NULL
                        ELSE NULL
                   END AS IsPurchased,
                   CASE WHEN item.user_id <> @viewerUserId
                        THEN buyer.display_name
                        ELSE NULL
                   END AS PurchasedByName
            FROM christmas_list_items AS item
            LEFT JOIN users AS buyer ON buyer.id = item.purchased_by_user_id
            WHERE item.user_id = @ownerUserId
              AND item.image_status <> 'uploading'
            ORDER BY item.created_at DESC;
            """;

        return await ApplicationTelemetry.TrackAsync("db.list_items.select", async () =>
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            var rows = await connection.QueryAsync<ChristmasListItemRow>(
                new CommandDefinition(sql, new { ownerUserId, viewerUserId }, cancellationToken: cancellationToken));
            return (IReadOnlyList<ChristmasListItemRow>)rows.AsList();
        });
    }

    public async Task PrepareUploadAsync(
        Guid itemId,
        Guid ownerUserId,
        string objectId,
        string contentType,
        long size,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO christmas_list_items (id, user_id, object_id, image_status)
            VALUES (@itemId, @ownerUserId, @objectId, 'uploading')
            ON CONFLICT (id) DO NOTHING;
            """,
            new { itemId, ownerUserId, objectId },
            transaction,
            cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO compression_jobs (
                item_id, source_object_id, expected_content_type, expected_size,
                status, upload_expires_at
            )
            VALUES (
                @itemId, @objectId, @contentType, @size,
                'awaiting_upload', now() + interval '15 minutes'
            )
            ON CONFLICT (item_id) DO NOTHING;
            """,
            new { itemId, objectId, contentType, size },
            transaction,
            cancellationToken: cancellationToken));

        var existing = await connection.QuerySingleOrDefaultAsync<PendingUpload>(
            new CommandDefinition(
                """
                SELECT item.id AS ItemId,
                       item.user_id AS OwnerUserId,
                       job.source_object_id AS ObjectId,
                       job.expected_content_type AS ExpectedContentType,
                       job.expected_size AS ExpectedSize,
                       job.status AS Status
                FROM christmas_list_items AS item
                JOIN compression_jobs AS job ON job.item_id = item.id
                WHERE item.id = @itemId;
                """,
                new { itemId },
                transaction,
                cancellationToken: cancellationToken));
        if (existing is null ||
            existing.OwnerUserId != ownerUserId ||
            existing.ObjectId != objectId ||
            existing.ExpectedContentType != contentType ||
            existing.ExpectedSize != size ||
            existing.Status != "awaiting_upload")
        {
            throw new InvalidOperationException("The upload ID has already been used.");
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<PendingUpload?> GetPendingUploadAsync(
        Guid itemId,
        Guid ownerUserId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT item.id AS ItemId,
                   item.user_id AS OwnerUserId,
                   job.source_object_id AS ObjectId,
                   job.expected_content_type AS ExpectedContentType,
                   job.expected_size AS ExpectedSize,
                   job.status AS Status
            FROM christmas_list_items AS item
            JOIN compression_jobs AS job ON job.item_id = item.id
            WHERE item.id = @itemId AND item.user_id = @ownerUserId;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<PendingUpload>(
            new CommandDefinition(
                sql,
                new { itemId, ownerUserId },
                cancellationToken: cancellationToken));
    }

    public async Task<bool> CompleteUploadAsync(
        Guid itemId,
        Guid ownerUserId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE compression_jobs AS job
            SET status = 'queued',
                available_at = now(),
                upload_expires_at = NULL,
                updated_at = now()
            FROM christmas_list_items AS item
            WHERE job.item_id = @itemId
              AND item.id = job.item_id
              AND item.user_id = @ownerUserId
              AND job.status = 'awaiting_upload';
            """,
            new { itemId, ownerUserId },
            transaction,
            cancellationToken: cancellationToken));

        if (affected == 1)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE christmas_list_items
                SET image_status = 'processing'
                WHERE id = @itemId AND user_id = @ownerUserId;
                """,
                new { itemId, ownerUserId },
                transaction,
                cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            return true;
        }

        var status = await connection.QuerySingleOrDefaultAsync<string>(
            new CommandDefinition(
                """
                SELECT job.status
                FROM compression_jobs AS job
                JOIN christmas_list_items AS item ON item.id = job.item_id
                WHERE job.item_id = @itemId AND item.user_id = @ownerUserId;
                """,
                new { itemId, ownerUserId },
                transaction,
                cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return status is "queued" or "processing" or "cleanup_pending" or "completed" or "failed";
    }

    public async Task<string?> AbortPendingUploadAsync(
        Guid itemId,
        Guid ownerUserId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<string>(
            new CommandDefinition(
                """
                UPDATE compression_jobs AS job
                SET status = 'abandoned_upload',
                    available_at = now(),
                    upload_expires_at = NULL,
                    lease_expires_at = NULL,
                    updated_at = now()
                FROM christmas_list_items AS item
                WHERE job.item_id = @itemId
                  AND item.id = job.item_id
                  AND item.user_id = @ownerUserId
                  AND job.status = 'awaiting_upload'
                RETURNING job.source_object_id;
                """,
                new { itemId, ownerUserId },
                cancellationToken: cancellationToken));
    }

    public async Task<bool> SetPurchasedAsync(
        Guid itemId,
        Guid viewerUserId,
        bool purchased,
        CancellationToken cancellationToken = default)
    {
        var sql = purchased
            ? """
              UPDATE christmas_list_items
              SET purchased_by_user_id = @viewerUserId, purchased_at = now()
              WHERE id = @itemId
                AND user_id <> @viewerUserId
                AND purchased_by_user_id IS NULL;
              """
            : """
              UPDATE christmas_list_items
              SET purchased_by_user_id = NULL, purchased_at = NULL
              WHERE id = @itemId
                AND user_id <> @viewerUserId
                AND purchased_by_user_id = @viewerUserId;
              """;

        return await ApplicationTelemetry.TrackAsync("db.list_items.purchase.update", async () =>
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            var affected = await connection.ExecuteAsync(
                new CommandDefinition(sql, new { itemId, viewerUserId }, cancellationToken: cancellationToken));
            return affected == 1;
        });
    }

    public async Task<IReadOnlyList<string>?> DeleteItemAsync(
        Guid itemId,
        Guid ownerUserId,
        CancellationToken cancellationToken = default)
    {
        return await ApplicationTelemetry.TrackAsync("db.list_items.delete", async () =>
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var compressionJob = await connection.QuerySingleOrDefaultAsync<PendingCompressionCleanup>(
                new CommandDefinition(
                    """
                    SELECT source_object_id AS SourceObjectId,
                           expected_content_type AS ExpectedContentType,
                           compressed_object_id AS CompressedObjectId
                    FROM compression_jobs
                    WHERE item_id = @itemId
                    FOR UPDATE;
                    """,
                    new { itemId },
                    transaction,
                    cancellationToken: cancellationToken));

            var itemObjectId = await connection.QuerySingleOrDefaultAsync<string>(
                new CommandDefinition(
                    """
                    SELECT object_id
                    FROM christmas_list_items
                    WHERE id = @itemId
                      AND user_id = @ownerUserId
                      AND image_status <> 'uploading'
                    FOR UPDATE;
                    """,
                    new { itemId, ownerUserId },
                    transaction,
                    cancellationToken: cancellationToken));
            if (itemObjectId is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }

            var objectIds = new HashSet<string>(StringComparer.Ordinal) { itemObjectId };
            if (compressionJob is not null)
            {
                objectIds.Add(compressionJob.SourceObjectId);
                if (compressionJob.CompressedObjectId is not null)
                {
                    objectIds.Add(compressionJob.CompressedObjectId);
                }

                var extension = compressionJob.ExpectedContentType == "image/png" ? "png" : "jpg";
                objectIds.Add($"compressed/{itemId:N}.{extension}");
            }

            foreach (var objectId in objectIds)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO r2_object_deletion_jobs (object_id)
                    VALUES (@objectId)
                    ON CONFLICT (object_id) DO NOTHING;
                    """,
                    new { objectId },
                    transaction,
                    cancellationToken: cancellationToken));
            }

            var deleted = await connection.ExecuteAsync(new CommandDefinition(
                """
                DELETE FROM christmas_list_items
                WHERE id = @itemId AND user_id = @ownerUserId;
                """,
                new { itemId, ownerUserId },
                transaction,
                cancellationToken: cancellationToken));
            if (deleted != 1)
            {
                throw new InvalidOperationException("The list item could not be removed.");
            }

            await transaction.CommitAsync(cancellationToken);
            return (IReadOnlyList<string>)objectIds.ToArray();
        });
    }

    private sealed class PendingCompressionCleanup
    {
        public string SourceObjectId { get; set; } = "";
        public string ExpectedContentType { get; set; } = "";
        public string? CompressedObjectId { get; set; }
    }
}

public sealed class ChristmasListItemRow
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public string ObjectId { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public string ImageStatus { get; set; } = "ready";
    public bool? IsPurchased { get; set; }
    public string? PurchasedByName { get; set; }
}
