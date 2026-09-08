using ContentPilot.Application.Abstractions;
using ContentPilot.Domain.Branding;
using ContentPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ContentPilot.Infrastructure.Branding;

/// <summary>
/// Ingests an upload and records it. The two halves are separate on purpose: the ingestor
/// decides whether bytes are acceptable and what they are, this decides where they live and
/// what the library knows about them.
/// </summary>
public sealed class AssetLibrary(
    AppDbContext db,
    IObjectStore store,
    IImageIngestor ingestor,
    ITenantContext tenantContext,
    ILogger<AssetLibrary> logger)
{
    public async Task<BrandAsset> AddAsync(
        Guid brandId,
        AssetKind kind,
        Stream content,
        string fileName,
        string? description,
        IEnumerable<string>? tags,
        CancellationToken ct = default)
    {
        var tenantId = tenantContext.RequireTenantId();

        if (!await db.Brands.AnyAsync(b => b.Id == brandId, ct))
        {
            throw new InvalidOperationException($"No brand '{brandId}' in the tenant in scope.");
        }

        var options = OptionsFor(kind);
        var ingested = await ingestor.IngestAsync(content, fileName, options, ct);

        // Content addressing means the same picture uploaded twice is one stored object.
        // Returning the existing row rather than a duplicate keeps the library honest and
        // keeps a re-upload from quietly doubling storage.
        var existing = await db.BrandAssets
            .FirstOrDefaultAsync(a => a.BrandId == brandId && a.Sha256 == ingested.Sha256 && a.Variant == null, ct);

        if (existing is not null)
        {
            logger.LogInformation(
                "Upload {FileName} matches existing asset {AssetId} byte for byte; reusing it.", fileName, existing.Id);

            if (existing.IsArchived)
            {
                existing.Restore();
                await db.SaveChangesAsync(ct);
            }

            return existing;
        }

        var key = ObjectKey.ForTenant(tenantId,
            $"brand/{brandId:N}/assets/{ingested.Sha256}.{ingested.Extension}");

        await using (var upload = new MemoryStream(ingested.Bytes, writable: false))
        {
            await store.PutAsync(key, upload, ingested.MediaType, ct);
        }

        var asset = new BrandAsset(
            tenantId, brandId, kind, fileName, key.Value, ingested.MediaType, ingested.Sha256,
            ingested.Bytes.Length, ingested.Width, ingested.Height, AssetOrigin.Upload, tags);

        asset.Describe(description);
        asset.SetPerceptualHash(ingested.PerceptualHash);
        asset.SetDominantColors(ingested.DominantColors);

        db.BrandAssets.Add(asset);

        if (ingested.ThumbnailBytes is { Length: > 0 })
        {
            await AddThumbnailAsync(asset, tenantId, brandId, ingested, ct);
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Stored {Kind} asset {AssetId} ({Width}x{Height}, {Bytes} bytes){Stripped}.",
            kind, asset.Id, asset.Width, asset.Height, asset.Bytes,
            ingested.StrippedMetadata.Count > 0
                ? $", stripped {string.Join(", ", ingested.StrippedMetadata)}"
                : string.Empty);

        return asset;
    }

    private async Task AddThumbnailAsync(
        BrandAsset parent, Guid tenantId, Guid brandId, IngestedImage ingested, CancellationToken ct)
    {
        var thumbnail = ingested.ThumbnailBytes
            ?? throw new ArgumentException("Called without a thumbnail to store.", nameof(ingested));

        var thumbKey = ObjectKey.ForTenant(tenantId,
            $"brand/{brandId:N}/derived/{ingested.Sha256}-thumb.{ingested.Extension}");

        await using var upload = new MemoryStream(thumbnail, writable: false);
        await store.PutAsync(thumbKey, upload, ingested.MediaType, ct);

        // The thumbnail's own content hash, not the parent's with a suffix: a content
        // address that is not the hash of the content is not a content address.
        var thumbSha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(thumbnail));

        db.BrandAssets.Add(BrandAsset.Derived(
            parent, "thumb", thumbKey.Value, ingested.MediaType,
            thumbSha, thumbnail.Length,
            ingested.ThumbnailWidth, ingested.ThumbnailHeight));
    }

    /// <summary>Opens the stored bytes. Used by the render pipeline, never by an agent.</summary>
    public async Task<(BrandAsset Asset, Stream Content)> OpenAsync(Guid assetId, CancellationToken ct = default)
    {
        var asset = await db.BrandAssets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == assetId, ct)
            ?? throw new InvalidOperationException($"No asset '{assetId}' in the tenant in scope.");

        return (asset, await store.GetAsync(ObjectKey.FromExisting(asset.StorageKey), ct));
    }

    public async Task<Uri> GetDownloadUrlAsync(Guid assetId, TimeSpan lifetime, CancellationToken ct = default)
    {
        var asset = await db.BrandAssets.AsNoTracking().FirstOrDefaultAsync(a => a.Id == assetId, ct)
            ?? throw new InvalidOperationException($"No asset '{assetId}' in the tenant in scope.");

        return await store.GetPresignedReadUrlAsync(ObjectKey.FromExisting(asset.StorageKey), lifetime, ct);
    }

    /// <summary>
    /// Limits differ by what the asset is for. A logo is small and needs transparency; a
    /// screenshot is tall and detailed and must not be downscaled into mush.
    /// </summary>
    private static IngestOptions OptionsFor(AssetKind kind) => kind switch
    {
        AssetKind.Logo => IngestOptions.Default with
        {
            MinWidth = 64,
            MinHeight = 32,
            MaxDimension = 2048,
            PreserveTransparency = true,
        },
        AssetKind.ProductScreenshot => IngestOptions.Default with
        {
            MinWidth = 320,
            MinHeight = 320,
            // Screenshots are composited at up to 2x into tall slots; detail matters here
            // more than anywhere else in the library.
            MaxDimension = 4096,
            PreserveTransparency = false,
        },
        AssetKind.Background => IngestOptions.Default with
        {
            MinWidth = 512,
            MinHeight = 512,
            MaxDimension = 2560,
            PreserveTransparency = false,
        },
        _ => IngestOptions.Default,
    };
}
