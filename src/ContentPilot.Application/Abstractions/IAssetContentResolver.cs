using ContentPilot.Rendering.Contracts;

namespace ContentPilot.Application.Abstractions;

/// <summary>
/// Resolves a brand asset's identifier to the actual bytes <c>SpecAssembler</c> needs.
/// Deliberately not part of <c>IBrandBrainReader</c>: a snapshot is composed once per
/// campaign and cached in memory for its lifetime, while a resolve happens once per asset
/// slot per render attempt and reaches object storage every time — different costs, and
/// object storage can fail transiently in a way a snapshot lookup does not.
/// </summary>
public interface IAssetContentResolver
{
    Task<ImagePayload> ResolveAsync(Guid assetId, CancellationToken ct = default);

    /// <summary>
    /// The common case: every asset slot <c>TemplateSelector</c> assigned for one candidate,
    /// resolved together. A dictionary in, a dictionary out, in the exact shape
    /// <c>SpecAssembler.Assemble</c> wants for its <c>assets</c> parameter.
    /// </summary>
    async Task<IReadOnlyDictionary<string, ImagePayload>> ResolveManyAsync(
        IReadOnlyDictionary<string, Guid> assetIdsBySlot,
        CancellationToken ct = default)
    {
        var result = new Dictionary<string, ImagePayload>(assetIdsBySlot.Count, StringComparer.Ordinal);

        foreach (var (slotId, assetId) in assetIdsBySlot)
        {
            result[slotId] = await ResolveAsync(assetId, ct);
        }

        return result;
    }
}

/// <summary>The asset id named by a slot assignment no longer resolves — deleted, or from another tenant.</summary>
public sealed class BrandAssetNotFoundException(Guid assetId)
    : Exception($"No brand asset '{assetId}' exists in the tenant in scope.")
{
    public Guid AssetId { get; } = assetId;
}
