using System.Runtime.CompilerServices;
using Amazon.S3;
using Amazon.S3.Model;
using ContentPilot.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ContentPilot.Infrastructure.Storage;

/// <summary>
/// S3-compatible object store. The same code runs against MinIO locally and Cloudflare
/// R2 in production; only configuration changes. Buckets are private without exception —
/// every read a browser performs goes through a short-lived presigned URL.
/// </summary>
public sealed class S3ObjectStore(
    IAmazonS3 client,
    S3Presigner presigner,
    IOptions<ObjectStorageOptions> options,
    ILogger<S3ObjectStore> logger) : IObjectStore
{
    private readonly ObjectStorageOptions _options = options.Value;

    public async Task PutAsync(ObjectKey key, Stream content, string contentType, CancellationToken ct = default)
    {
        var request = new PutObjectRequest
        {
            BucketName = _options.Bucket,
            Key = key.Value,
            InputStream = content,
            ContentType = contentType,
            AutoCloseStream = false,
        };

        await client.PutObjectAsync(request, ct);

        logger.LogDebug("Stored {Key} ({ContentType}).", key.Value, contentType);
    }

    public async Task<Stream> GetAsync(ObjectKey key, CancellationToken ct = default)
    {
        try
        {
            var response = await client.GetObjectAsync(_options.Bucket, key.Value, ct);
            return response.ResponseStream;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new FileNotFoundException($"Object '{key.Value}' was not found in bucket '{_options.Bucket}'.", key.Value, ex);
        }
    }

    public async Task<bool> ExistsAsync(ObjectKey key, CancellationToken ct = default)
    {
        try
        {
            await client.GetObjectMetadataAsync(_options.Bucket, key.Value, ct);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public Task DeleteAsync(ObjectKey key, CancellationToken ct = default) =>
        client.DeleteObjectAsync(_options.Bucket, key.Value, ct);

    public Task<Uri> GetPresignedReadUrlAsync(ObjectKey key, TimeSpan lifetime, CancellationToken ct = default)
    {
        if (lifetime <= TimeSpan.Zero || lifetime > _options.MaxPresignedUrlLifetime)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lifetime),
                lifetime,
                $"Presigned URL lifetime must be between zero and {_options.MaxPresignedUrlLifetime}.");
        }

        var request = new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = key.Value,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(lifetime),

            // The signer defaults to https regardless of ServiceURL, so a local MinIO
            // over http would hand out links that only fail at download time.
            Protocol = _options.UsesPlainHttp ? Protocol.HTTP : Protocol.HTTPS,
        };

        return Task.FromResult(new Uri(presigner.Client.GetPreSignedURL(request)));
    }

    public async IAsyncEnumerable<ObjectKey> ListAsync(string prefix, [EnumeratorCancellation] CancellationToken ct = default)
    {
        string? continuationToken = null;

        do
        {
            var response = await client.ListObjectsV2Async(
                new ListObjectsV2Request
                {
                    BucketName = _options.Bucket,
                    Prefix = prefix,
                    ContinuationToken = continuationToken,
                    MaxKeys = 1000,
                },
                ct);

            foreach (var item in response.S3Objects ?? [])
            {
                yield return ObjectKey.FromExisting(item.Key);
            }

            continuationToken = response.IsTruncated == true ? response.NextContinuationToken : null;
        }
        while (continuationToken is not null);
    }
}

/// <summary>
/// Wraps the S3 client used only for signing browser-facing URLs. A distinct type rather
/// than a second <see cref="IAmazonS3"/> registration, so the two can never be confused
/// at an injection site.
/// </summary>
public sealed record S3Presigner(IAmazonS3 Client);

public sealed class ObjectStorageOptions
{
    public const string SectionName = "ObjectStorage";

    public string Bucket { get; set; } = "contentpilot";

    /// <summary>
    /// Set for MinIO and R2; leave empty for real AWS S3. Must include a scheme — without
    /// one the AWS SDK silently assumes https and every request fails on TLS.
    /// </summary>
    public string? ServiceUrl { get; set; }

    /// <summary>
    /// The endpoint a browser can actually reach, when it differs from the one the server
    /// uses. Inside Docker the API talks to <c>http://minio:9000</c>, which no viewer can
    /// resolve — and the host is part of the signature, so a signed URL cannot simply be
    /// rewritten afterwards. It has to be signed against the public endpoint from the
    /// start. In production this is the R2 or S3 public hostname. Falls back to
    /// <see cref="ServiceUrl"/> when unset.
    /// </summary>
    public string? PublicServiceUrl { get; set; }

    public string? EffectivePresignUrl =>
        string.IsNullOrWhiteSpace(PublicServiceUrl) ? ServiceUrl : PublicServiceUrl;

    /// <summary>True only for a plain-http signing endpoint, which means local MinIO.</summary>
    public bool UsesPlainHttp =>
        !string.IsNullOrWhiteSpace(EffectivePresignUrl) &&
        Uri.TryCreate(EffectivePresignUrl, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttp;

    public bool HasValidServiceUrl => IsValidEndpoint(ServiceUrl) && IsValidEndpoint(PublicServiceUrl);

    private static bool IsValidEndpoint(string? value) =>
        string.IsNullOrWhiteSpace(value) ||
        (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
         (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps));

    public string? AccessKey { get; set; }

    public string? SecretKey { get; set; }

    public string Region { get; set; } = "us-east-1";

    /// <summary>MinIO needs path style; R2 and S3 do not.</summary>
    public bool ForcePathStyle { get; set; } = true;

    /// <summary>Created at startup in development only; never in production.</summary>
    public bool CreateBucketIfMissing { get; set; }

    /// <summary>
    /// Ceiling on any presigned URL. Fifteen minutes is long enough to click a download
    /// and short enough that a leaked link is worthless.
    /// </summary>
    public TimeSpan MaxPresignedUrlLifetime { get; set; } = TimeSpan.FromMinutes(15);

    public TimeSpan DefaultPresignedUrlLifetime { get; set; } = TimeSpan.FromMinutes(15);
}
