using System.Text.Json;
using ContentPilot.Rendering.Contracts;
using ContentPilot.Renderer.Engine;
using Microsoft.Extensions.Options;

namespace ContentPilot.Renderer.Video;

/// <summary>Loads and validates reel manifests independently of the frozen static catalog.</summary>
public sealed class ReelTemplateCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly Dictionary<string, ReelTemplateManifest> _templates = new(StringComparer.Ordinal);

    public ReelTemplateCatalog(IOptions<RendererOptions> options, ILogger<ReelTemplateCatalog> logger)
    {
        var directory = Path.Combine(options.Value.TemplateDirectory, "Reel");

        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Reel template directory '{directory}' is missing.");
        }

        foreach (var path in Directory.GetFiles(directory, "*.reel.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            var manifest = JsonSerializer.Deserialize<ReelTemplateManifest>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidOperationException($"Reel manifest '{path}' did not deserialise.");

            Validate(manifest, path);

            if (!_templates.TryAdd(manifest.TemplateId, manifest))
            {
                throw new InvalidOperationException(
                    $"More than one reel manifest declares template '{manifest.TemplateId}'.");
            }
        }

        if (_templates.Count == 0)
        {
            throw new InvalidOperationException($"No reel manifests were found in '{directory}'.");
        }

        logger.LogInformation(
            "Loaded {Count} reel templates: {Templates}.", _templates.Count, string.Join(", ", _templates.Keys));
    }

    public IReadOnlyCollection<ReelTemplateManifest> Manifests => _templates.Values.ToArray();

    public ReelTemplateManifest Get(string templateId) =>
        _templates.TryGetValue(templateId, out var manifest)
            ? manifest
            : throw new ReelRenderException($"No reel template with id '{templateId}'.");

    private static void Validate(ReelTemplateManifest manifest, string path)
    {
        if (string.IsNullOrWhiteSpace(manifest.TemplateId) || manifest.Version <= 0)
        {
            throw new InvalidOperationException($"Reel manifest '{path}' needs an id and a positive version.");
        }

        if (manifest.ColorSchemes.Count == 0 || manifest.Scenes.Count is < 3 or > 5)
        {
            throw new InvalidOperationException(
                $"Reel manifest '{path}' must offer a colour scheme and contain three to five scenes.");
        }

        var duplicateScenes = manifest.Scenes.GroupBy(s => s.Id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();

        if (duplicateScenes.Length > 0)
        {
            throw new InvalidOperationException(
                $"Reel manifest '{path}' reuses scene ids: {string.Join(", ", duplicateScenes)}.");
        }

        for (var index = 0; index < manifest.Scenes.Count; index++)
        {
            var scene = manifest.Scenes[index];
            var isLast = index == manifest.Scenes.Count - 1;

            if (string.IsNullOrWhiteSpace(scene.Id) || scene.TextSlots.Count == 0)
            {
                throw new InvalidOperationException($"Reel manifest '{path}' contains an unnamed or empty scene.");
            }

            if (scene.MinDurationSeconds < 1.2 ||
                scene.DefaultDurationSeconds < scene.MinDurationSeconds ||
                scene.DefaultDurationSeconds > scene.MaxDurationSeconds)
            {
                throw new InvalidOperationException(
                    $"Reel manifest '{path}' scene '{scene.Id}' has invalid duration bounds.");
            }

            if ((isLast && scene.TransitionSeconds != 0) ||
                (!isLast && (scene.TransitionSeconds <= 0 || scene.TransitionSeconds >= scene.MinDurationSeconds)))
            {
                throw new InvalidOperationException(
                    $"Reel manifest '{path}' scene '{scene.Id}' has an invalid transition overlap.");
            }

            foreach (var slot in scene.TextSlots)
            {
                if (slot.MaxChars <= 0 || slot.MaxLines <= 0 || (slot.ShrinkToFit && slot.MinFontPx <= 0))
                {
                    throw new InvalidOperationException(
                        $"Reel manifest '{path}' scene '{scene.Id}' slot '{slot.Id}' has an invalid budget.");
                }
            }

            foreach (var slot in scene.AssetSlots)
            {
                if (slot.Immutable && slot.MaxOcclusion > 0.25)
                {
                    throw new InvalidOperationException(
                        $"Reel manifest '{path}' scene '{scene.Id}' allows too much occlusion for '{slot.Id}'.");
                }
            }

            var duplicateSlots = scene.TextSlots.Select(s => s.Id)
                .Concat(scene.AssetSlots.Select(s => s.Id))
                .GroupBy(id => id, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToArray();

            if (duplicateSlots.Length > 0)
            {
                throw new InvalidOperationException(
                    $"Reel manifest '{path}' scene '{scene.Id}' reuses slots: {string.Join(", ", duplicateSlots)}.");
            }
        }

        if (manifest.SafeMode && manifest.Scenes.SelectMany(s => s.AssetSlots).Any(s => s.Required))
        {
            throw new InvalidOperationException(
                $"Safe-mode reel manifest '{path}' cannot require an external image asset.");
        }
    }
}
