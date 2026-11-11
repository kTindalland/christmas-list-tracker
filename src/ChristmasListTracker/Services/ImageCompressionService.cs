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
        await using var bufferedInput = new MemoryStream();
        await input.CopyToAsync(bufferedInput, cancellationToken);
        bufferedInput.Position = 0;

        var imageInfo = await Image.IdentifyAsync(bufferedInput, cancellationToken)
            ?? throw new InvalidDataException("This file is not a supported image.");
        if (imageInfo.Width > MaxImageDimension ||
            imageInfo.Height > MaxImageDimension ||
            (long)imageInfo.Width * imageInfo.Height > 40_000_000)
        {
            throw new InvalidDataException("This image is too large to process safely.");
        }

        bufferedInput.Position = 0;
        using var image = await Image.LoadAsync<Rgba32>(bufferedInput, cancellationToken);
        var format = image.Metadata.DecodedImageFormat?.Name;
        var isPng = string.Equals(format, "PNG", StringComparison.OrdinalIgnoreCase);
        var isJpeg = string.Equals(format, "JPEG", StringComparison.OrdinalIgnoreCase);
        if (!isPng && !isJpeg)
        {
            throw new InvalidDataException("Only JPEG and PNG screenshots are supported.");
        }

        if (image.Width > MaxStoredDimension || image.Height > MaxStoredDimension)
        {
            image.Mutate(operation => operation.Resize(new ResizeOptions
            {
                Mode = ResizeMode.Max,
                Size = new Size(MaxStoredDimension, MaxStoredDimension)
            }));
        }

        await using var output = new MemoryStream();
        if (isPng)
        {
            await image.SaveAsPngAsync(output, new PngEncoder
            {
                CompressionLevel = PngCompressionLevel.BestCompression
            }, cancellationToken);
            return new CompressedImage(output.ToArray(), "image/png", "png");
        }

        await image.SaveAsJpegAsync(output, new JpegEncoder { Quality = 92 }, cancellationToken);
        return new CompressedImage(output.ToArray(), "image/jpeg", "jpg");
    }
}

public sealed record CompressedImage(byte[] Content, string ContentType, string Extension);
