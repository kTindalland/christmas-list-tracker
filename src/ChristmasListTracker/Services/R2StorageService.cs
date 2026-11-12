using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using System.Diagnostics;

namespace ChristmasListTracker.Services;

public sealed class R2StorageService : IDisposable
{
    private readonly string _bucketName;
    private readonly AmazonS3Client? _client;

    public R2StorageService(IConfiguration configuration)
    {
        var serviceUrl = configuration["R2:ServiceUrl"];
        _bucketName = configuration["R2:BucketName"] ?? "";
        var accessKeyId = configuration["R2:AccessKeyId"];
        var secretAccessKey = configuration["R2:SecretAccessKey"];

        if (!string.IsNullOrWhiteSpace(serviceUrl) &&
            !string.IsNullOrWhiteSpace(_bucketName) &&
            !string.IsNullOrWhiteSpace(accessKeyId) &&
            !string.IsNullOrWhiteSpace(secretAccessKey))
        {
            _client = new AmazonS3Client(
                new BasicAWSCredentials(accessKeyId, secretAccessKey),
                new AmazonS3Config
                {
                    ServiceURL = serviceUrl,
                    ForcePathStyle = true,
                    AuthenticationRegion = configuration["R2:Region"] ?? "auto"
                });
        }
    }

    public async Task UploadAsync(
        string objectId,
        CompressedImage image,
        CancellationToken cancellationToken = default)
    {
        await ApplicationTelemetry.TrackAsync("r2.object.upload", async () =>
        {
            var activity = Activity.Current;
            activity?.SetTag("r2.object.size", image.Content.Length);
            activity?.SetTag("r2.content_type", image.ContentType);

            await using var stream = new MemoryStream(image.Content, writable: false);
            await GetClient().PutObjectAsync(new PutObjectRequest
            {
                BucketName = _bucketName,
                Key = objectId,
                InputStream = stream,
                ContentType = image.ContentType,
                DisablePayloadSigning = true,
                UseChunkEncoding = false
            }, cancellationToken);
        });
    }

    public async Task DeleteAsync(string objectId, CancellationToken cancellationToken = default)
    {
        await ApplicationTelemetry.TrackAsync(
            "r2.object.delete",
            () => GetClient().DeleteObjectAsync(_bucketName, objectId, cancellationToken));
    }

    public string CreateUploadUrl(string objectId, string contentType)
    {
        return GetClient().GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = _bucketName,
            Key = objectId,
            ContentType = contentType,
            Expires = DateTime.UtcNow.AddMinutes(5),
            Verb = HttpVerb.PUT
        });
    }

    public async Task<(long Size, string? ContentType)> GetObjectMetadataAsync(
        string objectId,
        CancellationToken cancellationToken = default)
    {
        return await ApplicationTelemetry.TrackAsync("r2.object.head", async () =>
        {
            var response = await GetClient().GetObjectMetadataAsync(
                _bucketName,
                objectId,
                cancellationToken);
            return (response.ContentLength, response.Headers.ContentType);
        });
    }

    public async Task<byte[]> DownloadAsync(string objectId, CancellationToken cancellationToken = default)
    {
        return await ApplicationTelemetry.TrackAsync("r2.object.download", async () =>
        {
            using var response = await GetClient().GetObjectAsync(
                _bucketName,
                objectId,
                cancellationToken);
            await using var output = new MemoryStream();
            await response.ResponseStream.CopyToAsync(output, cancellationToken);
            var bytes = output.ToArray();
            Activity.Current?.SetTag("r2.object.size", bytes.Length);
            return bytes;
        });
    }

    public string CreateReadUrl(string objectId)
    {
        return GetClient().GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = _bucketName,
            Key = objectId,
            Expires = DateTime.UtcNow.AddMinutes(15),
            Verb = HttpVerb.GET
        });
    }

    public void Dispose() => _client?.Dispose();

    private AmazonS3Client GetClient()
    {
        if (_client is null)
        {
            throw new InvalidOperationException(
                "R2 settings are incomplete. Configure R2:ServiceUrl, R2:BucketName, R2:AccessKeyId, and R2:SecretAccessKey.");
        }

        if (string.IsNullOrWhiteSpace(_bucketName))
        {
            throw new InvalidOperationException("Configure R2:BucketName before using image storage.");
        }

        return _client;
    }
}
