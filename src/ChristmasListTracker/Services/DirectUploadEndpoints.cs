using ChristmasListTracker.Models;

namespace ChristmasListTracker.Services;

public static class DirectUploadEndpoints
{
    public static IEndpointRouteBuilder MapDirectUploadEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/uploads/prepare", async (
            UploadPreparationRequest request,
            HttpContext context,
            ListRepository repository,
            R2StorageService storage,
            CancellationToken cancellationToken) =>
        {
            var userId = await GetCurrentUserIdAsync(context, repository, cancellationToken);
            if (userId is null)
            {
                return Results.Unauthorized();
            }

            if (request.UploadId == Guid.Empty ||
                request.Size <= 0 ||
                request.Size > ImageCompressionService.MaxUploadBytes ||
                request.ContentType is not ("image/jpeg" or "image/png"))
            {
                return Results.BadRequest(new { error = "Choose a JPEG or PNG image no larger than 20 MB." });
            }

            var itemId = request.UploadId;
            var extension = request.ContentType == "image/png" ? "png" : "jpg";
            var objectId = $"originals/{userId.Value}/{itemId:N}.{extension}";
            var url = storage.CreateUploadUrl(objectId, request.ContentType);

            await repository.PrepareUploadAsync(
                itemId,
                userId.Value,
                objectId,
                request.ContentType,
                request.Size,
                cancellationToken);

            return Results.Ok(new DirectUploadGrant(itemId, url, request.ContentType));
        });

        endpoints.MapPost("/api/uploads/{itemId:guid}/complete", async (
            Guid itemId,
            UploadCompletionRequest request,
            HttpContext context,
            ListRepository repository,
            R2StorageService storage,
            CancellationToken cancellationToken) =>
        {
            var userId = await GetCurrentUserIdAsync(context, repository, cancellationToken);
            if (userId is null)
            {
                return Results.Unauthorized();
            }

            var pendingUpload = await repository.GetPendingUploadAsync(itemId, userId.Value, cancellationToken);
            if (pendingUpload is null)
            {
                return Results.NotFound();
            }

            var wasAwaitingUpload = pendingUpload.Status == "awaiting_upload";
            if (wasAwaitingUpload)
            {
                if (!double.IsFinite(request.DurationMs) || request.DurationMs < 0 || request.DurationMs > 300_000)
                {
                    return Results.BadRequest(new { error = "The upload duration was invalid." });
                }

                var metadata = await storage.GetObjectMetadataAsync(pendingUpload.ObjectId, cancellationToken);
                if (metadata.Size != pendingUpload.ExpectedSize ||
                    !string.Equals(
                        metadata.ContentType,
                        pendingUpload.ExpectedContentType,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return Results.BadRequest(new { error = "The uploaded file did not match the selected image." });
                }
            }

            var completed = await repository.CompleteUploadAsync(itemId, userId.Value, cancellationToken);
            if (!completed)
            {
                return Results.Conflict(new { error = "This upload is no longer waiting for completion." });
            }

            if (wasAwaitingUpload)
            {
                using var activity = ApplicationTelemetry.ActivitySource.StartActivity("upload.client_to_r2");
                activity?.SetTag("upload.duration_ms", request.DurationMs);
                activity?.SetTag("upload.size", pendingUpload.ExpectedSize);
                activity?.SetTag("upload.content_type", pendingUpload.ExpectedContentType);
                ApplicationTelemetry.DirectUploadDuration.Record(
                    request.DurationMs,
                    new KeyValuePair<string, object?>("format", pendingUpload.ExpectedContentType),
                    new KeyValuePair<string, object?>("outcome", "success"));
            }

            return Results.Ok();
        });

        endpoints.MapPost("/api/uploads/{itemId:guid}/abort", async (
            Guid itemId,
            HttpContext context,
            ListRepository repository,
            CompressionJobRepository jobs,
            R2StorageService storage,
            CancellationToken cancellationToken) =>
        {
            var userId = await GetCurrentUserIdAsync(context, repository, cancellationToken);
            if (userId is null)
            {
                return Results.Unauthorized();
            }

            var objectId = await repository.AbortPendingUploadAsync(itemId, userId.Value, cancellationToken);
            if (objectId is null)
            {
                return Results.Conflict(new { error = "This upload can no longer be cancelled." });
            }

            await storage.DeleteAsync(objectId, cancellationToken);
            await jobs.DeleteAbandonedUploadAsync(itemId, cancellationToken);
            return Results.Ok();
        });

        return endpoints;
    }

    private static async Task<Guid?> GetCurrentUserIdAsync(
        HttpContext context,
        ListRepository repository,
        CancellationToken cancellationToken)
    {
        return Guid.TryParse(context.Request.Cookies["christmas-list-user"], out var userId) &&
               await repository.GetUserAsync(userId, cancellationToken) is not null
            ? userId
            : null;
    }
}

public sealed record UploadPreparationRequest(Guid UploadId, string FileName, string ContentType, long Size);
public sealed record UploadCompletionRequest(double DurationMs);
