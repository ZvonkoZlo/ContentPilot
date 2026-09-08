using System.Text.Json.Serialization;

namespace ContentPilot.Rendering.Contracts;

/// <summary>
/// The most important file in the system. Three components read it and none of them may
/// duplicate its knowledge: the template selector decides which templates are legal for a
/// content item, the copy brief derives per-slot character budgets from it, and
/// deterministic QA reads its thresholds. That is precisely why it is data rather than
/// behaviour spread across code.
/// </summary>
public sealed record TemplateManifest
{
    public required string TemplateId { get; init; }

    /// <summary>Bumped on any change that alters rendered output. A CreativeSpec pins one.</summary>
    public required int Version { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public required IReadOnlyList<TemplateContentType> ContentTypes { get; init; }

    public required IReadOnlyList<AspectRatio> AspectRatios { get; init; }

    /// <summary>Content pillars this template suits. Used for ranking, never for legality.</summary>
    public IReadOnlyList<string> PillarAffinity { get; init; } = [];

    /// <summary>
    /// The escalation ladder's last rung: generous budgets, solid brand background, no
    /// generated imagery. It must render cleanly for almost any input, because when it
    /// fails the item goes to a human.
    /// </summary>
    public bool SafeMode { get; init; }

    public required IReadOnlyList<TextSlot> TextSlots { get; init; }

    public IReadOnlyList<AssetSlot> AssetSlots { get; init; } = [];

    public IReadOnlyList<ColorScheme> ColorSchemes { get; init; } = [ColorScheme.LightOnDark];

    /// <summary>
    /// Whether the template wants a generated or supplied background image behind it.
    /// Safe-mode templates say no, which is what makes them cheap and reliable.
    /// </summary>
    public bool SupportsBackgroundImage { get; init; }

    public SafeAreas SafeAreas { get; init; } = SafeAreas.Default;

    public TextSlot? FindTextSlot(string id) =>
        TextSlots.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));

    public AssetSlot? FindAssetSlot(string id) =>
        AssetSlots.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));

    public bool Supports(AspectRatio ratio) => AspectRatios.Contains(ratio);
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TemplateContentType
{
    Static,
    CarouselSlide,
    ReelScene,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ColorScheme
{
    LightOnDark,
    DarkOnLight,
}

/// <summary>
/// A text slot with a hard character budget. The budget is what the copywriter writes
/// against, and what deterministic QA checks — an overflowing headline is caught before a
/// vision model is ever asked to look at the image.
/// </summary>
public sealed record TextSlot
{
    public required string Id { get; init; }

    public required string Role { get; init; }

    /// <summary>Hard ceiling. The copywriter is briefed with it; the validator enforces it.</summary>
    public required int MaxChars { get; init; }

    public required int MaxLines { get; init; }

    public bool Required { get; init; } = true;

    /// <summary>
    /// A bounded font-size reduction that absorbs near-miss overflow without spending a
    /// retry. The render report records when it fired so QA can still flag abuse.
    /// </summary>
    public bool ShrinkToFit { get; init; }

    /// <summary>Floor for <see cref="ShrinkToFit"/>, in CSS pixels at 1x.</summary>
    public int MinFontPx { get; init; }

    /// <summary>
    /// Budgets are tuned for English. Croatian, German and other languages run longer for
    /// the same meaning, so the brief scales the budget down by this factor. Applied by
    /// the copy brief, never silently by the renderer.
    /// </summary>
    public double LongLanguageFactor { get; init; } = 0.85;

    public int BudgetFor(string languageTag) =>
        IsCompactLanguage(languageTag) ? MaxChars : (int)Math.Floor(MaxChars * LongLanguageFactor);

    private static bool IsCompactLanguage(string languageTag) =>
        languageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A slot that holds an image. When <see cref="Immutable"/> is set the pixels must survive
/// the render untouched apart from uniform scaling — that invariant is enforced at spec
/// validation, at render time, and again by the fidelity check afterwards.
/// </summary>
public sealed record AssetSlot
{
    public required string Id { get; init; }

    public required AssetKind Kind { get; init; }

    public bool Required { get; init; } = true;

    /// <summary>
    /// No generated asset may occupy this slot, no filters, no blend modes, uniform scale
    /// only. This is what makes "the AI redrew my UI" structurally impossible rather than
    /// something QA has to notice.
    /// </summary>
    public bool Immutable { get; init; }

    /// <summary>
    /// Fraction of the slot that other elements may cover. Verified exactly from the mask
    /// render, so it is a measurement rather than a heuristic.
    /// </summary>
    public double MaxOcclusion { get; init; }

    /// <summary>Minimum rendered width in device pixels; below it a logo stops being legible.</summary>
    public int MinWidthPx { get; init; }

    /// <summary>Clear space around a logo, as a fraction of its width.</summary>
    public double ClearSpaceRatio { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AssetKind
{
    ProductScreenshot,
    Logo,
    Photo,
    Background,
}

/// <summary>
/// Platform chrome overlaps the frame: Instagram's header and action bar, TikTok's caption
/// band. Nothing that must be read may sit inside these margins.
/// </summary>
public sealed record SafeAreas
{
    public static readonly SafeAreas Default = new();

    public double Top { get; init; } = 0.06;

    public double Bottom { get; init; } = 0.12;

    public double Left { get; init; } = 0.05;

    public double Right { get; init; } = 0.05;
}

/// <summary>
/// The MVP supports 4:5 and 9:16, plus 1:1 where a template can carry it. Every additional
/// ratio is a full pass over every template, so the list stays short on purpose.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AspectRatio
{
    /// <summary>1080x1350 — the default feed post.</summary>
    FourFive,

    /// <summary>1080x1080.</summary>
    OneOne,

    /// <summary>1080x1920 — reels and stories.</summary>
    NineSixteen,
}

public static class AspectRatioExtensions
{
    public static (int Width, int Height) Dimensions(this AspectRatio ratio) => ratio switch
    {
        AspectRatio.FourFive => (1080, 1350),
        AspectRatio.OneOne => (1080, 1080),
        AspectRatio.NineSixteen => (1080, 1920),
        _ => throw new ArgumentOutOfRangeException(nameof(ratio), ratio, "Unsupported aspect ratio."),
    };

    /// <summary>
    /// The frame is authored at half the output size and screenshotted at deviceScaleFactor
    /// 2, so CSS pixel values in templates stay readable while output stays retina.
    /// </summary>
    public static (int Width, int Height) CssDimensions(this AspectRatio ratio)
    {
        var (w, h) = ratio.Dimensions();
        return (w / 2, h / 2);
    }

    public static string ToCssAspect(this AspectRatio ratio) => ratio switch
    {
        AspectRatio.FourFive => "4 / 5",
        AspectRatio.OneOne => "1 / 1",
        AspectRatio.NineSixteen => "9 / 16",
        _ => throw new ArgumentOutOfRangeException(nameof(ratio), ratio, "Unsupported aspect ratio."),
    };
}
