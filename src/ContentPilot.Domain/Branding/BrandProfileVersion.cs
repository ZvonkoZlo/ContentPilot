using ContentPilot.Domain.Common;

namespace ContentPilot.Domain.Branding;

/// <summary>
/// An immutable snapshot of everything the Brand Brain contained at the moment a campaign
/// started, with a content hash over it.
/// <para>
/// This is not bookkeeping. Without it you cannot answer the only question that matters
/// when a post reads badly six weeks later — what did the agents actually see? A brand
/// profile that has been edited since is not evidence; a hashed snapshot is.
/// </para>
/// </summary>
public sealed class BrandProfileVersion : Entity, ITenantOwned, IAppendOnly
{
    private BrandProfileVersion()
    {
        SnapshotJson = null!;
        ContentHash = null!;
    }

    public BrandProfileVersion(Guid tenantId, Guid brandId, string snapshotJson, string contentHash, DateTimeOffset createdAt)
    {
        TenantId = Guard.NotEmpty(tenantId);
        BrandId = Guard.NotEmpty(brandId);
        SnapshotJson = Guard.NotBlank(snapshotJson);
        ContentHash = Guard.NotBlank(contentHash);
        CreatedAt = createdAt;
    }

    public Guid TenantId { get; private set; }

    public Guid BrandId { get; private set; }

    /// <summary>The serialised <c>BrandSnapshot</c>. Read back wholesale, never queried into.</summary>
    public string SnapshotJson { get; private set; }

    /// <summary>
    /// SHA-256 over the canonical snapshot. Identical content produces an identical hash,
    /// so an unchanged brand reuses its version instead of accumulating duplicates.
    /// </summary>
    public string ContentHash { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
}
