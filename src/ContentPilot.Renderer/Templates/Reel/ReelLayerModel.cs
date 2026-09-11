using ContentPilot.Rendering.Contracts;

namespace ContentPilot.Renderer.Templates.Reel;

public enum ReelLayer
{
    Background,
    Product,
    Text,
    Composite,
}

public sealed class ReelLayerModel(
    ReelTemplateManifest template,
    ReelSceneManifest scene,
    ReelSpec spec,
    ReelSceneSpec request,
    ReelLayer layer)
{
    public ReelTemplateManifest Template { get; } = template;

    public ReelSceneManifest Scene { get; } = scene;

    public ReelSpec Spec { get; } = spec;

    public ReelSceneSpec Request { get; } = request;

    public ReelLayer Layer { get; } = layer;

    public bool IncludesBackground => Layer is ReelLayer.Background or ReelLayer.Composite;

    public bool IncludesProduct => Layer is ReelLayer.Product or ReelLayer.Composite;

    public bool IncludesText => Layer is ReelLayer.Text or ReelLayer.Composite;

    public bool HasText(string slotId) =>
        Request.Text.TryGetValue(slotId, out var value) && !string.IsNullOrWhiteSpace(value);

    public string Text(string slotId)
    {
        if (Scene.FindTextSlot(slotId) is null)
        {
            throw new InvalidOperationException(
                $"Reel template '{Template.TemplateId}' scene '{Scene.Id}' rendered undeclared text slot '{slotId}'.");
        }

        return Request.Text.TryGetValue(slotId, out var value) ? value : string.Empty;
    }

    public bool HasAsset(string slotId) => Request.Assets.ContainsKey(slotId);

    public string? Asset(string slotId)
    {
        if (Scene.FindAssetSlot(slotId) is null)
        {
            throw new InvalidOperationException(
                $"Reel template '{Template.TemplateId}' scene '{Scene.Id}' rendered undeclared asset slot '{slotId}'.");
        }

        return Request.Assets.TryGetValue(slotId, out var payload) ? payload.ToDataUri() : null;
    }
}
