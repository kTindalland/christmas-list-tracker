using ChristmasListTracker.Models;

namespace ChristmasListTracker.Services;

public sealed class ChristmasListService(
    ListRepository repository,
    R2StorageService storage,
    R2ObjectDeletionJobRepository deletionJobs,
    ILogger<ChristmasListService> logger)
{
    public Task<IReadOnlyList<FamilyUser>> GetUsersAsync(CancellationToken cancellationToken = default) =>
        ApplicationTelemetry.TrackAsync(
            "christmas_list.users.get",
            () => repository.GetUsersAsync(cancellationToken));

    public async Task<IReadOnlyList<ChristmasListItem>> GetListAsync(
        Guid ownerUserId,
        Guid viewerUserId,
        CancellationToken cancellationToken = default)
    {
        return await ApplicationTelemetry.TrackAsync("christmas_list.items.get", async () =>
        {
            var rows = await repository.GetListAsync(ownerUserId, viewerUserId, cancellationToken);
            return rows.Select(row => new ChristmasListItem(
                row.Id,
                row.OwnerUserId,
                storage.CreateReadUrl(row.ObjectId),
                row.CreatedAt,
                row.ImageStatus,
                row.IsPurchased,
                row.PurchasedByName)).ToArray();
        });
    }

    public Task<bool> SetPurchasedAsync(
        Guid itemId,
        Guid viewerUserId,
        bool purchased,
        CancellationToken cancellationToken = default) =>
        ApplicationTelemetry.TrackAsync(
            "christmas_list.item.purchase.update",
            () => repository.SetPurchasedAsync(itemId, viewerUserId, purchased, cancellationToken));

    public async Task<DeleteListItemResult?> DeleteItemAsync(
        Guid itemId,
        Guid ownerUserId,
        CancellationToken cancellationToken = default)
    {
        return await ApplicationTelemetry.TrackAsync("christmas_list.item.delete", async () =>
        {
            var objectIds = await repository.DeleteItemAsync(itemId, ownerUserId, cancellationToken);
            if (objectIds is null)
            {
                return null;
            }

            var cleanupPending = false;
            foreach (var objectId in objectIds)
            {
                try
                {
                    await storage.DeleteAsync(objectId, cancellationToken);
                    await deletionJobs.MarkDeletedAsync(objectId, cancellationToken);
                }
                catch (Exception exception) when (
                    exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    cleanupPending = true;
                    logger.LogError(exception, "R2 cleanup is pending for deleted list item object {ObjectId}.", objectId);
                }
            }

            return new DeleteListItemResult(cleanupPending);
        });
    }
}

public sealed record DeleteListItemResult(bool ImageCleanupPending);
