using ChristmasListTracker.Models;

namespace ChristmasListTracker.Services;

public sealed class ChristmasListService(
    ListRepository repository,
    ImageCompressionService imageCompression,
    R2StorageService storage,
    ILogger<ChristmasListService> logger)
{
    public Task<IReadOnlyList<FamilyUser>> GetUsersAsync(CancellationToken cancellationToken = default) =>
        repository.GetUsersAsync(cancellationToken);

    public async Task<IReadOnlyList<ChristmasListItem>> GetListAsync(
        Guid ownerUserId,
        Guid viewerUserId,
        CancellationToken cancellationToken = default)
    {
        var rows = await repository.GetListAsync(ownerUserId, viewerUserId, cancellationToken);
        return rows.Select(row => new ChristmasListItem(
            row.Id,
            row.OwnerUserId,
            storage.CreateReadUrl(row.ObjectId),
            row.CreatedAt,
            row.IsPurchased,
            row.PurchasedByName)).ToArray();
    }

    public async Task UploadAsync(
        Guid ownerUserId,
        Stream content,
        string objectId,
        CancellationToken cancellationToken = default)
    {
        var compressedImage = await imageCompression.CompressAsync(content, cancellationToken);
        var storedObjectId = $"{objectId}.{compressedImage.Extension}";
        await storage.UploadAsync(storedObjectId, compressedImage, cancellationToken);

        try
        {
            await repository.AddItemAsync(ownerUserId, storedObjectId, cancellationToken);
        }
        catch
        {
            try
            {
                await storage.DeleteAsync(storedObjectId, CancellationToken.None);
            }
            catch (Exception cleanupException)
            {
                logger.LogError(cleanupException, "Failed to clean up R2 object {ObjectId} after the database insert failed.", storedObjectId);
            }

            throw;
        }
    }

    public Task<bool> SetPurchasedAsync(
        Guid itemId,
        Guid viewerUserId,
        bool purchased,
        CancellationToken cancellationToken = default) =>
        repository.SetPurchasedAsync(itemId, viewerUserId, purchased, cancellationToken);
}
