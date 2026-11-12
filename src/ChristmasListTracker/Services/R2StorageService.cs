using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

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
    }

    public async Task DeleteAsync(string objectId, CancellationToken cancellationToken = default)
    {
        await GetClient().DeleteObjectAsync(_bucketName, objectId, cancellationToken);
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
