using ChristmasListTracker.Models;

namespace ChristmasListTracker.Services;

public sealed class ChristmasListService(
    ListRepository repository,
    R2StorageService storage)
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
}
