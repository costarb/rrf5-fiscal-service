using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Rrf5.Fiscal.Application.Errors;
using Rrf5.Fiscal.Application.Regulatory;

namespace Rrf5.Fiscal.Infrastructure.Storage;

/// <summary>Object storage settings (SeaweedFS locally; the cloud provider later).</summary>
public sealed class ObjectStorageOptions
{
    public const string Section = "ObjectStorage";

    public Uri? ServiceUrl { get; set; }

    public string? AccessKey { get; set; }

    public string? SecretKey { get; set; }

    public string Bucket { get; set; } = "rrf5-regulatory";

    /// <summary>Retention applied when the bucket supports object lock.</summary>
    public int RetentionYears { get; set; } = 10;
}

/// <summary>
/// T035 over the S3 API. Keys are content-addressed (<c>artifacts/sha256/&lt;hash&gt;</c>).
/// The bucket is created with object lock and versioning when the store supports
/// them; each object then gets COMPLIANCE retention. When the store does not, the
/// object is still write-once at the application level (an existing key is never
/// overwritten) and the record says <c>UNAVAILABLE</c>, so the gap stays visible.
/// </summary>
public sealed class S3ArtifactStore : IArtifactStore, IDisposable
{
    private readonly ObjectStorageOptions _options;
    private readonly Lazy<AmazonS3Client> _client;
    private readonly SemaphoreSlim _bucketGate = new(1, 1);
    private bool? _objectLock;

    public S3ArtifactStore(ObjectStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _client = new Lazy<AmazonS3Client>(() =>
        {
            if (options.ServiceUrl is null || string.IsNullOrEmpty(options.AccessKey) || string.IsNullOrEmpty(options.SecretKey))
            {
                throw new DomainException(ErrorCodes.Unavailable, "Object storage is not configured.");
            }

            return new AmazonS3Client(
                new BasicAWSCredentials(options.AccessKey, options.SecretKey),
                new AmazonS3Config
                {
                    ServiceURL = options.ServiceUrl.ToString(),
                    ForcePathStyle = true,
                    AuthenticationRegion = "us-east-1",

                    // Plain bodies: some S3-compatible stores (SeaweedFS 3.97) persist the
                    // aws-chunked framing of optional trailing checksums as object content.
                    RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
                    ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
                });
        });
    }

    public static string KeyFor(string sha256) => $"artifacts/sha256/{sha256}";

    public async Task<StoredObject> PutAsync(string sha256, byte[] content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var key = KeyFor(sha256);
        try
        {
            var locked = await EnsureBucketAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var head = await _client.Value.GetObjectMetadataAsync(_options.Bucket, key, cancellationToken).ConfigureAwait(false);
                return new StoredObject(key, head.VersionId, locked ? "COMPLIANCE" : "UNAVAILABLE");
            }
            catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
            {
                // Not stored yet.
            }

            using var stream = new MemoryStream(content, writable: false);
            var request = new PutObjectRequest
            {
                BucketName = _options.Bucket,
                Key = key,
                InputStream = stream,
                ContentType = "application/octet-stream",
                AutoCloseStream = false,
                UseChunkEncoding = false,
            };
            request.Metadata.Add("sha256", sha256);
            if (locked)
            {
                request.ObjectLockMode = ObjectLockMode.Compliance;
                request.ObjectLockRetainUntilDate = DateTime.UtcNow.AddYears(_options.RetentionYears);
            }

            var response = await _client.Value.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
            return new StoredObject(key, response.VersionId, locked ? "COMPLIANCE" : "UNAVAILABLE");
        }
        catch (Exception exception) when (exception is AmazonServiceException or HttpRequestException or AmazonClientException)
        {
            throw new DomainException(ErrorCodes.Unavailable, "Object storage is unavailable.", exception);
        }
    }

    public async Task<byte[]> GetAsync(string objectKey, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _client.Value.GetObjectAsync(_options.Bucket, objectKey, cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            return buffer.ToArray();
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            throw new DomainException(ErrorCodes.ArtifactIntegrity, innerException: exception);
        }
        catch (Exception exception) when (exception is AmazonServiceException or HttpRequestException or AmazonClientException)
        {
            throw new DomainException(ErrorCodes.Unavailable, "Object storage is unavailable.", exception);
        }
    }

    /// <summary>Creates the bucket (with object lock when supported) and reports whether objects can be locked.</summary>
    public async Task<bool> EnsureBucketAsync(CancellationToken cancellationToken)
    {
        if (_objectLock is { } known)
        {
            return known;
        }

        await _bucketGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_objectLock is { } again)
            {
                return again;
            }

            var client = _client.Value;
            if (!await Amazon.S3.Util.AmazonS3Util.DoesS3BucketExistV2Async(client, _options.Bucket).ConfigureAwait(false))
            {
                try
                {
                    await client.PutBucketAsync(new PutBucketRequest { BucketName = _options.Bucket, ObjectLockEnabledForBucket = true }, cancellationToken).ConfigureAwait(false);
                }
                catch (AmazonS3Exception)
                {
                    await client.PutBucketAsync(new PutBucketRequest { BucketName = _options.Bucket }, cancellationToken).ConfigureAwait(false);
                }
            }

            try
            {
                await client.PutBucketVersioningAsync(new PutBucketVersioningRequest
                {
                    BucketName = _options.Bucket,
                    VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled },
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (AmazonS3Exception)
            {
                // Versioning not supported by this store; write-once keys still apply.
            }

            try
            {
                var configuration = await client.GetObjectLockConfigurationAsync(
                    new GetObjectLockConfigurationRequest { BucketName = _options.Bucket }, cancellationToken).ConfigureAwait(false);
                _objectLock = configuration.ObjectLockConfiguration?.ObjectLockEnabled == ObjectLockEnabled.Enabled;
            }
            catch (AmazonS3Exception)
            {
                _objectLock = false;
            }

            return _objectLock.Value;
        }
        finally
        {
            _bucketGate.Release();
        }
    }

    public void Dispose()
    {
        _bucketGate.Dispose();
        if (_client.IsValueCreated)
        {
            _client.Value.Dispose();
        }
    }
}
