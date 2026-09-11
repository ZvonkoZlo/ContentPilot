using ContentPilot.Application.Abstractions;
using ContentPilot.Infrastructure.Persistence;
using ContentPilot.Rendering.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ContentPilot.Infrastructure.Branding;

/// <summary>
/// Reads one <c>BrandAsset</c> row for its storage key and media type, then streams the
/// bytes back from <see cref="IObjectStore"/> and hands them to <c>SpecAssembler</c> as an
/// <see cref="ImagePayload"/>. The tenant query filter on <c>AppDbContext</c> means an asset
/// id from another tenant simply does not resolve, the same guarantee
/// <see cref="BrandBrainReader"/> relies on.
/// </summary>
public sealed class AssetContentResolver(AppDbContext db, IObjectStore store) : IAssetContentResolver
{
    public async Task<ImagePayload> ResolveAsync(Guid assetId, CancellationToken ct = default)
    {
        var asset = await db.BrandAssets
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == assetId, ct)
            ?? throw new BrandAssetNotFoundException(assetId);

        var key = ObjectKey.FromExisting(asset.StorageKey);

        await using var stream = await store.GetAsync(key, ct);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);

        return ImagePayload.FromBytes(buffer.ToArray(), asset.MediaType);
    }
}
