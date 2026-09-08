using ContentPilot.Domain.Common;

namespace ContentPilot.Domain.Branding;

/// <summary>
/// A file in the brand's asset library. Rows are never mutated: a re-upload creates a new
/// row, and derived variants (thumbnails, rasterised logos) are separate rows pointing at
/// their parent.
/// <para>
/// The reason is the invariant the whole platform rests on — a product screenshot that
/// reaches a render must be the exact bytes the operator uploaded. An asset you can edit in
/// place is an asset you cannot make that promise about.
/// </para>
/// </summary>
public sealed class BrandAsset : Entity, ITenantOwned, IAuditable
{
    private readonly List<string> _tags = [];
    private readonly List<string> _dominantColors = [];

    private BrandAsset()
    {
        FileName = null!;
        StorageKey = null!;
        MediaType = null!;
        Sha256 = null!;
    }

    public BrandAsset(
        Guid tenantId,
        Guid brandId,
        AssetKind kind,
        string fileName,
        string storageKey,
        string mediaType,
        string sha256,
        long bytes,
        int width,
        int height,
        AssetOrigin origin,
        IEnumerable<string>? tags = null)
    {
        TenantId = Guard.NotEmpty(tenantId);
        BrandId = Guard.NotEmpty(brandId);
        Kind = kind;
        FileName = Guard.MaxLength(Guard.NotBlank(fileName), 300);
        StorageKey = Guard.NotBlank(storageKey);
        MediaType = Guard.NotBlank(mediaType);
        Sha256 = Guard.NotBlank(sha256);
        Bytes = bytes;
        Width = width;
        Height = height;
        Origin = origin;

        if (tags is not null)
        {
            _tags.AddRange(tags.Select(Normalise).Where(t => t.Length > 0).Distinct());
        }
    }

    public Guid TenantId { get; private set; }

    public Guid BrandId { get; private set; }

    public AssetKind Kind { get; private set; }

    public string FileName { get; private set; }

    /// <summary>Content-addressed, so uploading the same logo twice costs nothing.</summary>
    public string StorageKey { get; private set; }

    public string MediaType { get; private set; }

    public string Sha256 { get; private set; }

    public long Bytes { get; private set; }

    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>
    /// Where the pixels came from. Only <see cref="AssetOrigin.Upload"/> may fill an
    /// immutable template slot — that check is what makes "the AI redrew my UI" impossible
    /// rather than merely unlikely.
    /// </summary>
    public AssetOrigin Origin { get; private set; }

    /// <summary>Set for thumbnails and rasterised variants; null for an original.</summary>
    public Guid? ParentAssetId { get; private set; }

    public string? Variant { get; private set; }

    /// <summary>Used later to spot near-duplicate uploads and to avoid reusing the same photo.</summary>
    public string? PerceptualHash { get; private set; }

    /// <summary>Sampled on ingest so a background can be matched to an asset without re-reading it.</summary>
    public IReadOnlyList<string> DominantColors => _dominantColors;

    public IReadOnlyList<string> Tags => _tags;

    public string? Description { get; private set; }

    public bool IsArchived { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }

    public double AspectRatio => Height == 0 ? 0 : (double)Width / Height;

    public static BrandAsset Derived(BrandAsset parent, string variant, string storageKey, string mediaType, string sha256, long bytes, int width, int height) =>
        new(parent.TenantId, parent.BrandId, parent.Kind, parent.FileName, storageKey, mediaType, sha256, bytes, width, height, parent.Origin)
        {
            ParentAssetId = parent.Id,
            Variant = Guard.NotBlank(variant),
        };

    public void SetPerceptualHash(string? hash) => PerceptualHash = hash;

    public void SetDominantColors(IEnumerable<string> colors)
    {
        _dominantColors.Clear();
        _dominantColors.AddRange(colors.Where(c => VisualIdentity.IsHexColor(c)).Distinct().Take(6));
    }

    public void Describe(string? description) =>
        Description = string.IsNullOrWhiteSpace(description) ? null : Guard.MaxLength(description.Trim(), 500);

    public void Retag(IEnumerable<string> tags)
    {
        _tags.Clear();
        _tags.AddRange(tags.Select(Normalise).Where(t => t.Length > 0).Distinct());
    }

    /// <summary>
    /// Archiving rather than deleting: an asset may still be referenced by a campaign that
    /// was already produced, and that history must stay explainable.
    /// </summary>
    public void Archive() => IsArchived = true;

    public void Restore() => IsArchived = false;

    private static string Normalise(string tag) => tag.Trim().ToLowerInvariant();
}

public enum AssetKind
{
    /// <summary>Real product UI. Composited, never generated, never edited.</summary>
    ProductScreenshot = 0,
    Logo = 1,
    Photo = 2,

    /// <summary>Generated or supplied backdrop. The one asset kind a model may produce.</summary>
    Background = 3,

    /// <summary>Something the brand published before. Reference material for tone, not for reuse.</summary>
    PriorCreative = 4,
}

public enum AssetOrigin
{
    Upload = 0,

    /// <summary>Produced by the platform from an upload: a thumbnail, a rasterised SVG.</summary>
    Derived = 1,

    /// <summary>Produced by an image model. Never valid in an immutable slot.</summary>
    Generated = 2,
}
