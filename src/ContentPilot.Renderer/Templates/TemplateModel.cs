using ContentPilot.Rendering.Contracts;

namespace ContentPilot.Renderer.Templates;

/// <summary>
/// What a template component is handed. It exposes slot lookups rather than the raw
/// request so a template cannot invent a slot the manifest does not declare — the manifest
/// stays the single source of truth about what a template contains.
/// </summary>
public sealed class TemplateModel(TemplateManifest manifest, RenderImageRequest request)
{
    public TemplateManifest Manifest { get; } = manifest;

    public RenderImageRequest Request { get; } = request;

    public BrandTokens Brand => Request.Brand;

    public ColorScheme Scheme => Request.ColorScheme;

    public AspectRatio Ratio => Request.AspectRatio;

    public bool IsTall => Ratio == AspectRatio.NineSixteen;

    public bool IsSquare => Ratio == AspectRatio.OneOne;

    /// <summary>
    /// Copy for a declared text slot. An undeclared slot is a template bug, not a missing
    /// value, so it throws rather than rendering empty.
    /// </summary>
    public string Text(string slotId)
    {
        if (Manifest.FindTextSlot(slotId) is null)
        {
            throw new InvalidOperationException(
                $"Template '{Manifest.TemplateId}' rendered slot '{slotId}', which its manifest does not declare.");
        }

        return Request.Text.TryGetValue(slotId, out var value) ? value : string.Empty;
    }

    public bool HasText(string slotId) =>
        Request.Text.TryGetValue(slotId, out var value) && !string.IsNullOrWhiteSpace(value);

    /// <summary>A data URI for a declared asset slot, or null when the slot is unfilled.</summary>
    public string? Asset(string slotId)
    {
        if (Manifest.FindAssetSlot(slotId) is null)
        {
            throw new InvalidOperationException(
                $"Template '{Manifest.TemplateId}' rendered asset slot '{slotId}', which its manifest does not declare.");
        }

        return Request.Assets.TryGetValue(slotId, out var payload) ? payload.ToDataUri() : null;
    }

    public bool HasAsset(string slotId) => Request.Assets.ContainsKey(slotId);

    /// <summary>
    /// True when the slot is declared immutable. Templates use it to stamp the
    /// <c>data-immutable</c> attribute, which the stylesheet then locks down.
    /// </summary>
    public bool IsImmutable(string slotId) =>
        Manifest.FindAssetSlot(slotId)?.Immutable == true;
}
