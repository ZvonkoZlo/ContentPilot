using ContentPilot.Domain.Common;

namespace ContentPilot.Domain.Content;

/// <summary>
/// One attempt's rendered output. Every attempt's asset is kept until retention, not only
/// the promoted one — when an item runs out of retries, the best attempt is promoted rather
/// than discarded, and that only works if the earlier attempts' assets still exist to point
/// at.
/// <para>
/// The pixels themselves live in object storage; this row is the reference plus the
/// measurements that matter without opening the file — the same split <c>AgentRun</c> and
/// <c>BrandAsset</c> already make.
/// </para>
/// </summary>
public sealed class ContentAsset : Entity, ITenantOwned, IAppendOnly
{
    private ContentAsset()
    {
        StorageKey = null!;
        MediaType = null!;
        Sha256 = null!;
    }

    public ContentAsset(
        Guid tenantId,
        Guid contentItemId,
        int attempt,
        ContentAssetKind kind,
        string storageKey,
        string mediaType,
        string sha256,
        long bytes,
        DateTimeOffset createdAt,
        int width = 0,
        int height = 0,
        string? metaJson = null)
    {
        TenantId = Guard.NotEmpty(tenantId);
        ContentItemId = Guard.NotEmpty(contentItemId);
        Attempt = Guard.InRange(attempt, 1, 1000);
        Kind = kind;
        StorageKey = Guard.NotBlank(storageKey);
        MediaType = Guard.NotBlank(mediaType);
        Sha256 = Guard.NotBlank(sha256);
        Bytes = bytes;
        Width = width;
        Height = height;
        MetaJson = metaJson;
        CreatedAt = createdAt;
    }

    public Guid TenantId { get; private set; }

    public Guid ContentItemId { get; private set; }

    public int Attempt { get; private set; }

    public ContentAssetKind Kind { get; private set; }

    public string StorageKey { get; private set; }

    public string MediaType { get; private set; }

    public string Sha256 { get; private set; }

    public long Bytes { get; private set; }

    /// <summary>Zero for kinds without pixel dimensions, such as a caption or a script.</summary>
    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>Kind-specific extras: slide index for a carousel, duration for a video.</summary>
    public string? MetaJson { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
}

public enum ContentAssetKind
{
    Image = 0,

    /// <summary>One slide of a carousel; <see cref="ContentAsset.MetaJson"/> carries its ordinal.</summary>
    CarouselSlide = 1,
    Video = 2,

    /// <summary>The extracted first frame, used for keyframe QA and as a thumbnail.</summary>
    Cover = 3,
}
