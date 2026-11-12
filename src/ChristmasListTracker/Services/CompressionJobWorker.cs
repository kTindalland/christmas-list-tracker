using System.Diagnostics;
using ChristmasListTracker.Models;

namespace ChristmasListTracker.Services;

public sealed class CompressionJobWorker(
    CompressionJobRepository repository,
    R2StorageService storage,
    ImageCompressionService imageCompression,
    ILogger<CompressionJobWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var job = await repository.ClaimNextAsync(stoppingToken);
                if (job is null)
                {
                    await Task.Delay(PollInterval, stoppingToken);
                    continue;
                }

                await ProcessAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Compression job worker iteration failed.");
                await Task.Delay(PollInterval, stoppingToken);
            }
        }
    }

    private async Task ProcessAsync(CompressionJob job, CancellationToken cancellationToken)
    {
        await ApplicationTelemetry.TrackAsync("compression_job.process", async () =>
        {
            Activity.Current?.SetTag("compression_job.status", job.Status);

            if (job.Status == "abandoned_upload")
            {
                await ProcessAbandonedUploadAsync(job, cancellationToken);
                return;
            }

            if (job.Status == "cleanup_pending")
            {
                await ProcessCleanupAsync(job, cancellationToken);
                return;
            }

            try
            {
                var original = await storage.DownloadAsync(job.SourceObjectId, cancellationToken);
                await using var input = new MemoryStream(original, writable: false);
                var compressedImage = await imageCompression.CompressAsync(input, cancellationToken);
                var compressedObjectId = $"compressed/{job.ItemId:N}.{compressedImage.Extension}";
                await storage.UploadAsync(compressedObjectId, compressedImage, cancellationToken);
                await repository.PromoteToCompressedAsync(job, compressedObjectId, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogError(exception, "Compression job failed for list item {ItemId}.", job.ItemId);
                await repository.RetryProcessingAsync(
                    job,
                    exception.Message,
                    exception is InvalidDataException,
                    cancellationToken);
            }
        });
    }

    private async Task ProcessCleanupAsync(CompressionJob job, CancellationToken cancellationToken)
    {
        try
        {
            await storage.DeleteAsync(job.SourceObjectId, cancellationToken);
            await repository.MarkCompletedAsync(job.ItemId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(exception, "Original image cleanup failed for list item {ItemId}.", job.ItemId);
            await repository.RetryCleanupAsync(job, exception.Message, cancellationToken);
        }
    }

    private async Task ProcessAbandonedUploadAsync(CompressionJob job, CancellationToken cancellationToken)
    {
        try
        {
            await storage.DeleteAsync(job.SourceObjectId, cancellationToken);
            await repository.DeleteAbandonedUploadAsync(job.ItemId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(exception, "Expired upload cleanup failed for list item {ItemId}.", job.ItemId);
            await repository.RetryCleanupAsync(job, exception.Message, cancellationToken);
        }
    }
}
