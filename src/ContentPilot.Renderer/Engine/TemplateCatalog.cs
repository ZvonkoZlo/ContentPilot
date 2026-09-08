using System.Text.Json;
using ContentPilot.Rendering.Contracts;
using ContentPilot.Renderer.Templates.Static;
using Microsoft.Extensions.Options;

namespace ContentPilot.Renderer.Engine;

/// <summary>
/// Maps a template id to its manifest and its component. Manifests are JSON on disk so
/// slot budgets can be tuned without a rebuild; components are types so the compiler still
/// checks the markup. The registry below is the one place the two are tied together, and a
/// mismatch fails at startup rather than at render time.
/// </summary>
public sealed class TemplateCatalog
{
    private static readonly IReadOnlyDictionary<string, Type> Components = new Dictionary<string, Type>(StringComparer.Ordinal)
    {
        ["safe-mode"] = typeof(SafeMode),
        ["phone-floating"] = typeof(PhoneFloating),
        ["hook-overlay"] = typeof(HookOverlay),
        ["feature-highlight"] = typeof(FeatureHighlight),
        ["testimonial"] = typeof(Testimonial),
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly Dictionary<string, TemplateEntry> _templates = new(StringComparer.Ordinal);

    public TemplateCatalog(IOptions<RendererOptions> options, ILogger<TemplateCatalog> logger)
    {
        var directory = options.Value.TemplateDirectory;

        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Template directory '{directory}' is missing.");
        }

        foreach (var path in Directory.GetFiles(directory, "*.manifest.json", SearchOption.AllDirectories).OrderBy(p => p))
        {
            var manifest = JsonSerializer.Deserialize<TemplateManifest>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidOperationException($"Manifest '{path}' did not deserialise.");

            if (!Components.TryGetValue(manifest.TemplateId, out var component))
            {
                throw new InvalidOperationException(
                    $"Manifest '{path}' declares template '{manifest.TemplateId}', which no component implements. " +
                    "Add it to TemplateCatalog.Components.");
            }

            Validate(manifest, path);

            _templates[manifest.TemplateId] = new TemplateEntry(manifest, component);
        }

        var missing = Components.Keys.Except(_templates.Keys, StringComparer.Ordinal).ToArray();

        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"Components without a manifest: {string.Join(", ", missing)}. Every template must declare its slots.");
        }

        logger.LogInformation(
            "Loaded {Count} templates: {Templates}.", _templates.Count, string.Join(", ", _templates.Keys));
    }

    public IReadOnlyCollection<TemplateManifest> Manifests =>
        _templates.Values.Select(t => t.Manifest).ToArray();

    public bool TryGet(string templateId, out TemplateEntry entry) =>
        _templates.TryGetValue(templateId, out entry!);

    public TemplateEntry Get(string templateId) =>
        _templates.TryGetValue(templateId, out var entry)
            ? entry
            : throw new TemplateNotFoundException(templateId);

    /// <summary>
    /// Manifest mistakes are silent and expensive: a budget of zero produces empty copy, a
    /// shrink floor above the design size shrinks nothing. They are caught at startup.
    /// </summary>
    private static void Validate(TemplateManifest manifest, string path)
    {
        if (manifest.AspectRatios.Count == 0)
        {
            throw new InvalidOperationException($"Manifest '{path}' supports no aspect ratio.");
        }

        if (manifest.TextSlots.Count == 0)
        {
            throw new InvalidOperationException($"Manifest '{path}' declares no text slots.");
        }

        foreach (var slot in manifest.TextSlots)
        {
            if (slot.MaxChars <= 0 || slot.MaxLines <= 0)
            {
                throw new InvalidOperationException(
                    $"Manifest '{path}' slot '{slot.Id}' has a non-positive budget.");
            }

            if (slot.ShrinkToFit && slot.MinFontPx <= 0)
            {
                throw new InvalidOperationException(
                    $"Manifest '{path}' slot '{slot.Id}' opts into shrink-to-fit without a font floor.");
            }
        }

        foreach (var slot in manifest.AssetSlots)
        {
            if (slot.Immutable && slot.MaxOcclusion > 0.25)
            {
                throw new InvalidOperationException(
                    $"Manifest '{path}' slot '{slot.Id}' is immutable but tolerates {slot.MaxOcclusion:P0} occlusion. " +
                    "An immutable asset must stay visible.");
            }
        }

        var duplicates = manifest.TextSlots.Select(s => s.Id)
            .Concat(manifest.AssetSlots.Select(s => s.Id))
            .GroupBy(id => id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();

        if (duplicates.Length > 0)
        {
            throw new InvalidOperationException(
                $"Manifest '{path}' reuses slot ids: {string.Join(", ", duplicates)}.");
        }
    }
}

public sealed record TemplateEntry(TemplateManifest Manifest, Type ComponentType);

public sealed class TemplateNotFoundException(string templateId)
    : Exception($"No template with id '{templateId}'.")
{
    public string TemplateId { get; } = templateId;
}
