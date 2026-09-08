namespace ContentPilot.Application.Abstractions;

/// <summary>
/// Every byte the platform stores or produces goes through here: uploaded assets,
/// rendered images and reels, campaign packages, archived model payloads.
/// Deliberately five methods wide so swapping MinIO for R2 stays trivial.
/// </summary>
public interface IObjectStore
{
    Task PutAsync(ObjectKey key, Stream content, string contentType, CancellationToken ct = default);

    Task<Stream> GetAsync(ObjectKey key, CancellationToken ct = default);

    Task<bool> ExistsAsync(ObjectKey key, CancellationToken ct = default);

    Task DeleteAsync(ObjectKey key, CancellationToken ct = default);

    /// <summary>
    /// Mints a short-lived read URL. Buckets are private always; this is the only way
    /// a browser ever reaches a stored object, and only after an authorisation check.
    /// </summary>
    Task<Uri> GetPresignedReadUrlAsync(ObjectKey key, TimeSpan lifetime, CancellationToken ct = default);

    IAsyncEnumerable<ObjectKey> ListAsync(string prefix, CancellationToken ct = default);
}

/// <summary>
/// A storage key that is always tenant-prefixed by construction, so a bucket policy can
/// enforce isolation later and no caller can accidentally address another tenant's data.
/// </summary>
public readonly record struct ObjectKey
{
    private ObjectKey(string value) => Value = value;

    public string Value { get; }

    public static ObjectKey ForTenant(Guid tenantId, string relativePath)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant identifier must not be empty.", nameof(tenantId));
        }

        var path = Normalise(relativePath);
        return new ObjectKey($"t/{tenantId:N}/{path}");
    }

    /// <summary>
    /// Only for keys the store itself owns and that are provably tenant-prefixed
    /// already (listing results, values read back from the database).
    /// </summary>
    public static ObjectKey FromExisting(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Storage key must not be blank.", nameof(value));
        }

        return new ObjectKey(value.Trim());
    }

    public static string TenantPrefix(Guid tenantId) => $"t/{tenantId:N}/";

    public override string ToString() => Value;

    public static implicit operator string(ObjectKey key) => key.Value;

    private static string Normalise(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("Relative path must not be blank.", nameof(relativePath));
        }

        var path = relativePath.Replace('\\', '/').Trim().TrimStart('/');

        if (path.Length == 0 || path.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("Relative path must not escape its tenant prefix.", nameof(relativePath));
        }

        return path;
    }
}
