using System.Diagnostics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ChristmasListTracker.Services;

public sealed class ImageCompressionService
{
    public const long MaxUploadBytes = 20 * 1024 * 1024;
    public const int MaxImageDimension = 12_000;
    private const int MaxStoredDimension = 3_000;

    public async Task<CompressedImage> CompressAsync(Stream input, CancellationToken cancellationToken = default)
    {
        using var activity = ApplicationTelemetry.ActivitySource.StartActivity("image.compress");
        var stopwatch = Stopwatch.StartNew();
        var inputSize = input.CanSeek ? input.Length - input.Position : 0;
        var outcome = "error";
        string? imageFormat = null;
        var outputSize = 0;

        try
        {
            await using var bufferedInput = new MemoryStream();
            await ApplicationTelemetry.TrackAsync(
                "image.stream.read",
                () => input.CopyToAsync(bufferedInput, cancellationToken));
            inputSize = bufferedInput.Length;
            bufferedInput.Position = 0;

            var imageInfo = await ApplicationTelemetry.TrackAsync(
                "image.identify",
                () => Image.IdentifyAsync(bufferedInput, cancellationToken))
                ?? throw new InvalidDataException("This file is not a supported image.");
            if (imageInfo.Width > MaxImageDimension ||
                imageInfo.Height > MaxImageDimension ||
                (long)imageInfo.Width * imageInfo.Height > 40_000_000)
            {
                throw new InvalidDataException("This image is too large to process safely.");
            }

            bufferedInput.Position = 0;
            using var image = await ApplicationTelemetry.TrackAsync(
                "image.decode",
                () => Image.LoadAsync<Rgba32>(bufferedInput, cancellationToken));
            var originalWidth = image.Width;
            var originalHeight = image.Height;
            var format = image.Metadata.DecodedImageFormat?.Name;
            var isPng = string.Equals(format, "PNG", StringComparison.OrdinalIgnoreCase);
            var isJpeg = string.Equals(format, "JPEG", StringComparison.OrdinalIgnoreCase);
            if (!isPng && !isJpeg)
            {
                throw new InvalidDataException("Only JPEG and PNG screenshots are supported.");
            }
            imageFormat = isPng ? "png" : "jpeg";

            if (image.Width > MaxStoredDimension || image.Height > MaxStoredDimension)
            {
                using var resizeActivity = ApplicationTelemetry.ActivitySource.StartActivity("image.resize");
                var resizeTimer = Stopwatch.StartNew();
                try
                {
                    image.Mutate(operation => operation.Resize(new ResizeOptions
                    {
                        Mode = ResizeMode.Max,
                        Size = new Size(MaxStoredDimension, MaxStoredDimension)
                    }));
                    resizeActivity?.SetTag("image.width", image.Width);
                    resizeActivity?.SetTag("image.height", image.Height);
                }
                catch (Exception exception)
                {
                    ApplicationTelemetry.RecordException(resizeActivity, exception);
                    throw;
                }
                finally
                {
                    resizeTimer.Stop();
                    ApplicationTelemetry.OperationDuration.Record(
                        resizeTimer.Elapsed.TotalMilliseconds,
                        new KeyValuePair<string, object?>("operation", "image.resize"),
                        new KeyValuePair<string, object?>("outcome", resizeActivity?.Status == ActivityStatusCode.Error ? "error" : "success"));
                }
            }

            await using var output = new MemoryStream();
            if (isPng)
            {
                await ApplicationTelemetry.TrackAsync("image.encode", async () =>
                {
                    await image.SaveAsPngAsync(output, new PngEncoder
                    {
                        CompressionLevel = PngCompressionLevel.BestCompression
                    }, cancellationToken);
                });
                var content = output.ToArray();
                outputSize = content.Length;
                outcome = "success";
                AddImageTags(
                    activity,
                    imageFormat,
                    image.Width,
                    image.Height,
                    originalWidth,
                    originalHeight,
                    inputSize,
                    outputSize);
                return new CompressedImage(content, "image/png", "png");
            }

            await ApplicationTelemetry.TrackAsync(
                "image.encode",
                () => image.SaveAsJpegAsync(output, new JpegEncoder { Quality = 92 }, cancellationToken));
            var jpegContent = output.ToArray();
            outputSize = jpegContent.Length;
            outcome = "success";
            AddImageTags(
                activity,
                imageFormat,
                image.Width,
                image.Height,
                originalWidth,
                originalHeight,
                inputSize,
                outputSize);
            return new CompressedImage(jpegContent, "image/jpeg", "jpg");
        }
        catch (Exception exception)
        {
            activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
            activity?.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
            {
                { "exception.type", exception.GetType().FullName }
            }));
            throw;
        }
        finally
        {
            stopwatch.Stop();
            activity?.SetTag("operation.duration_ms", stopwatch.Elapsed.TotalMilliseconds);
            ApplicationTelemetry.OperationDuration.Record(
                stopwatch.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("operation", "image.compress"),
                new KeyValuePair<string, object?>("outcome", outcome));
            if (inputSize > 0)
            {
                ApplicationTelemetry.ImageSize.Record(
                    inputSize,
                    new KeyValuePair<string, object?>("stage", "input"),
                    new KeyValuePair<string, object?>("format", imageFormat ?? "unknown"));
            }

            if (outputSize > 0)
            {
                ApplicationTelemetry.ImageSize.Record(
                    outputSize,
                    new KeyValuePair<string, object?>("stage", "compressed"),
                    new KeyValuePair<string, object?>("format", imageFormat ?? "unknown"));
            }
        }
    }

    private static void AddImageTags(
        Activity? activity,
        string? format,
        int width,
        int height,
        int originalWidth,
        int originalHeight,
        long inputSize,
        int outputSize)
    {
        activity?.SetTag("image.format", format);
        activity?.SetTag("image.width", width);
        activity?.SetTag("image.height", height);
        activity?.SetTag("image.original_width", originalWidth);
        activity?.SetTag("image.original_height", originalHeight);
        activity?.SetTag("image.input_size", inputSize);
        activity?.SetTag("image.output_size", outputSize);
    }
}

public sealed record CompressedImage(byte[] Content, string ContentType, string Extension);
