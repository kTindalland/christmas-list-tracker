using ChristmasListTracker.Models;
using Dapper;
using Npgsql;

namespace ChristmasListTracker.Services;

public sealed class ListRepository(NpgsqlDataSource dataSource)
{
    public async Task<IReadOnlyList<FamilyUser>> GetUsersAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var users = await connection.QueryAsync<FamilyUser>(
            new CommandDefinition(
                "SELECT id, display_name AS DisplayName FROM users ORDER BY display_name",
                cancellationToken: cancellationToken));
        return users.AsList();
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
            ORDER BY item.created_at DESC;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<ChristmasListItemRow>(
            new CommandDefinition(sql, new { ownerUserId, viewerUserId }, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<Guid> AddItemAsync(
        Guid ownerUserId,
        string objectId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO christmas_list_items (user_id, object_id)
            VALUES (@ownerUserId, @objectId)
            RETURNING id;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<Guid>(
            new CommandDefinition(sql, new { ownerUserId, objectId }, cancellationToken: cancellationToken));
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

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(sql, new { itemId, viewerUserId }, cancellationToken: cancellationToken));
        return affected == 1;
    }
}

public sealed class ChristmasListItemRow
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public string ObjectId { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public bool? IsPurchased { get; set; }
    public string? PurchasedByName { get; set; }
}
