using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContentPilot.Application.Brand;
using ContentPilot.Rendering.Contracts;

namespace ContentPilot.Application.Agents;

/// <summary>
/// Turns a chosen template, the copy written for it, and the brand's tokens into the exact
/// request the renderer will execute — the SpecAssembly step, and the last piece of glue the
/// item pipeline needed. Like <see cref="TemplateSelector"/>, this is deterministic
/// arithmetic and lookups, not a model call: every input has already been decided by an
/// earlier step, and nothing here makes a creative choice.
/// <para>
/// Image bytes are supplied already resolved, as <see cref="ImagePayload"/> values keyed by
/// slot id, rather than fetched here. Fetching bytes means reaching object storage, which is
/// an infrastructure concern this pure, easily-tested class has no business owning — the
/// caller resolves <see cref="TemplateSelector.Candidate.AssetAssignments"/> to bytes first.
/// </para>
/// </summary>
public static class SpecAssembler
{
    private static readonly JsonSerializerOptions HashJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static RenderImageRequest Assemble(
        TemplateManifest manifest,
        AspectRatio ratio,
        CopySet copy,
        BrandSnapshot brand,
        string language,
        IReadOnlyDictionary<string, ImagePayload> assets,
        ColorScheme? colorScheme = null,
        RenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(copy);
        ArgumentNullException.ThrowIfNull(brand);
        ArgumentNullException.ThrowIfNull(assets);

        if (!manifest.Supports(ratio))
        {
            throw new ArgumentException(
                $"Template '{manifest.TemplateId}' does not support {ratio}.", nameof(ratio));
        }

        var missingAssets = manifest.AssetSlots
            .Where(slot => slot.Required && !assets.ContainsKey(slot.Id))
            .Select(slot => slot.Id)
            .ToArray();

        if (missingAssets.Length > 0)
        {
            // TemplateSelector already proved a real asset exists for every required slot
            // before this template became a candidate at all — reaching this with one still
            // missing means the caller failed to resolve bytes for an assignment it was
            // given, which is a bug in the caller, not a runtime condition to route around.
            throw new ArgumentException(
                $"Template '{manifest.TemplateId}' requires asset(s) not supplied: {string.Join(", ", missingAssets)}.",
                nameof(assets));
        }

        // Sorted rather than left in whatever order the model or the caller produced them,
        // so two logically identical specs hash identically regardless of iteration order —
        // the whole point of pinning a hash on CreativeSpec is that it be stable.
        var text = copy.Slots
            .OrderBy(slot => slot.Id, StringComparer.Ordinal)
            .ToDictionary(slot => slot.Id, slot => slot.Text, StringComparer.Ordinal);

        var orderedAssets = assets
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        return new RenderImageRequest
        {
            TemplateId = manifest.TemplateId,
            TemplateVersion = manifest.Version,
            AspectRatio = ratio,
            ColorScheme = colorScheme ?? manifest.ColorSchemes[0],
            Brand = ToBrandTokens(brand),
            Text = text,
            Assets = orderedAssets,
            Language = language,
            Options = options ?? new RenderOptions(),
        };
    }

    /// <summary>
    /// The renderer's design tokens are already the shape <c>VisualIdentity</c> was written
    /// to produce — this is a rename, not a translation.
    /// </summary>
    public static BrandTokens ToBrandTokens(BrandSnapshot brand) => new()
    {
        Name = brand.Name,
        PrimaryColor = brand.Visual.PrimaryColor,
        SecondaryColor = brand.Visual.SecondaryColor,
        AccentColor = brand.Visual.AccentColor,
        DarkColor = brand.Visual.DarkColor,
        LightColor = brand.Visual.LightColor,
        HeadingFont = brand.Visual.HeadingFont,
        BodyFont = brand.Visual.BodyFont,
        CornerRadius = brand.Visual.CornerRadius,
    };

    /// <summary>
    /// SHA-256 over the request exactly as it will be sent. Two attempts whose assembled
    /// request hashes the same rendered the same input — the property <c>CreativeSpec</c>
    /// exists to make checkable.
    /// </summary>
    public static string ComputeHash(RenderImageRequest request)
    {
        var json = JsonSerializer.Serialize(request, HashJson);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));

        return Convert.ToHexStringLower(bytes);
    }
}
