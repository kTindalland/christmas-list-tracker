namespace ChristmasListTracker.Models;

public sealed record CompressionJob(
    Guid ItemId,
    string SourceObjectId,
    string ExpectedContentType,
    long ExpectedSize,
    string Status,
    int AttemptCount,
    string? CompressedObjectId);

public sealed record PendingUpload(
    Guid ItemId,
    Guid OwnerUserId,
    string ObjectId,
    string ExpectedContentType,
    long ExpectedSize,
    string Status);

public sealed record DirectUploadGrant(Guid ItemId, string Url, string ContentType);
