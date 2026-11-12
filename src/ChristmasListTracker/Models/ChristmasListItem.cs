namespace ChristmasListTracker.Models;

public sealed record ChristmasListItem(
    Guid Id,
    Guid OwnerUserId,
    string ImageUrl,
    DateTime CreatedAt,
    string ImageStatus,
    bool? IsPurchased,
    string? PurchasedByName);
