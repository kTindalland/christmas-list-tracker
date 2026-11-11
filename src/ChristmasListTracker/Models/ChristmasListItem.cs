namespace ChristmasListTracker.Models;

public sealed record ChristmasListItem(
    Guid Id,
    Guid OwnerUserId,
    string ImageUrl,
    DateTime CreatedAt,
    bool? IsPurchased,
    string? PurchasedByName);
