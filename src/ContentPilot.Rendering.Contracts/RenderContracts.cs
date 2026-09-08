using System.Text.Json.Serialization;

namespace ContentPilot.Rendering.Contracts;

/// <summary>
/// Everything the renderer needs to produce one image, and nothing it could fetch itself.
/// Assets travel as bytes rather than URLs on purpose: the renderer executes
/// attacker-influenceable HTML with its network blocked, so it must never be asked to
/// resolve an address supplied by anyone.
/// </summary>
public sealed record RenderImageRequest
{
    public required string TemplateId { get; init; }

    /// <summary>Pins the template version the spec was built against. Null means latest.</summary>
    public int? TemplateVersion { get; init; }

    public required AspectRatio AspectRatio { get; init; }

    public ColorScheme ColorScheme { get; init; } = ColorScheme.LightOnDark;

    public required BrandTokens Brand { get; init; }

    /// <summary>Slot id to copy. Every required slot in the manifest must be present.</summary>
    public required IReadOnlyDictionary<string, string> Text { get; init; }

    /// <summary>Slot id to image payload.</summary>
    public IReadOnlyDictionary<string, ImagePayload> Assets { get; init; } =
        new Dictionary<string, ImagePayload>();

    /// <summary>BCP-47 tag. Drives typographic details such as quotation marks and hyphenation.</summary>
    public string Language { get; init; } = "en";

    public RenderOptions Options { get; init; } = new();
}

public sealed record RenderOptions
{
    /// <summary>
    /// Mask renders are needed only when an immutable asset slot has to be verified. They
    /// cost roughly 200 ms because the page is already loaded.
    /// </summary>
    public bool IncludeMasks { get; init; } = true;

    public ImageFormat Format { get; init; } = ImageFormat.Png;

    /// <summary>Ignored for PNG.</summary>
    public int JpegQuality { get; init; } = 90;

    /// <summary>
    /// Renders slot outlines and safe-area guides over the image. Never used in production
    /// output — this exists so a human can see why a template misbehaved.
    /// </summary>
    public bool DebugOverlay { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ImageFormat
{
    Png,
    Jpeg,
    Webp,
}

/// <summary>An image travelling over the wire, base64 encoded with its media type.</summary>
public sealed record ImagePayload
{
    public required string MediaType { get; init; }

    public required string Base64 { get; init; }

    public static ImagePayload FromBytes(ReadOnlySpan<byte> bytes, string mediaType) =>
        new() { MediaType = mediaType, Base64 = Convert.ToBase64String(bytes) };

    public byte[] ToBytes() => Convert.FromBase64String(Base64);

    public string ToDataUri() => $"data:{MediaType};base64,{Base64}";
}

/// <summary>
/// The brand's visual identity, flattened into what a stylesheet actually needs. The
/// renderer turns these into CSS custom properties; templates never hard-code a colour.
/// </summary>
public sealed record BrandTokens
{
    public required string Name { get; init; }

    public required string PrimaryColor { get; init; }

    public string? SecondaryColor { get; init; }

    public string? AccentColor { get; init; }

    /// <summary>Deep brand-appropriate ground for light-on-dark schemes.</summary>
    public string DarkColor { get; init; } = "#101418";

    /// <summary>Near-white ground for dark-on-light schemes.</summary>
    public string LightColor { get; init; } = "#F7F8FA";

    /// <summary>One of the families baked into the renderer image.</summary>
    public string HeadingFont { get; init; } = "Archivo";

    public string BodyFont { get; init; } = "Inter";

    /// <summary>Corner radius in CSS pixels, at the template's authoring scale.</summary>
    public int CornerRadius { get; init; } = 14;
}

public sealed record RenderImageResponse
{
    public required string TemplateId { get; init; }

    public required int TemplateVersion { get; init; }

    public required ImagePayload Image { get; init; }

    /// <summary>Slot id to mask render, present only for immutable asset slots.</summary>
    public IReadOnlyDictionary<string, ImagePayload> Masks { get; init; } =
        new Dictionary<string, ImagePayload>();

    public required RenderReport Report { get; init; }
}

/// <summary>
/// What the browser knew at render time. This is the whole reason deterministic QA is
/// cheap and exact: the layout engine already measured every box, so overflow and
/// occlusion are read out rather than inferred from pixels.
/// </summary>
public sealed record RenderReport
{
    public required int Width { get; init; }

    public required int Height { get; init; }

    public required double DeviceScaleFactor { get; init; }

    public required IReadOnlyList<SlotMeasurement> Slots { get; init; }

    /// <summary>
    /// Fonts the page actually resolved. A missing family silently falls back and produces
    /// a subtly wrong render that QA may well pass, so it is asserted rather than assumed.
    /// </summary>
    public required IReadOnlyList<string> FontsLoaded { get; init; }

    public required long RenderDurationMs { get; init; }

    /// <summary>Requests the page attempted despite the network being blocked. Must be empty.</summary>
    public IReadOnlyList<string> BlockedRequests { get; init; } = [];
}

public sealed record SlotMeasurement
{
    public required string SlotId { get; init; }

    public required BoundingBox Box { get; init; }

    /// <summary>True when the content is taller or wider than its box: a clipped slot.</summary>
    public required bool Overflows { get; init; }

    public required int LineCount { get; init; }

    /// <summary>Final size after any shrink-to-fit, in device pixels.</summary>
    public double FontSizePx { get; init; }

    /// <summary>Set when shrink-to-fit had to intervene. Repeated use means budgets are wrong.</summary>
    public bool ShrinkApplied { get; init; }

    public string? ForegroundColor { get; init; }

    /// <summary>Median luminance of the pixels actually behind the slot, 0 to 1.</summary>
    public double? BackdropLuminance { get; init; }

    /// <summary>WCAG contrast ratio against that backdrop. Below 4.5 is a finding.</summary>
    public double? ContrastRatio { get; init; }

    /// <summary>Fraction of the slot covered by later-painted elements, from the mask render.</summary>
    public double Occlusion { get; init; }

    /// <summary>True when any part of the slot sits inside the platform safe-area margins.</summary>
    public bool BreaksSafeArea { get; init; }
}

public sealed record BoundingBox
{
    public required double X { get; init; }

    public required double Y { get; init; }

    public required double Width { get; init; }

    public required double Height { get; init; }

    public double AspectRatio => Height <= 0 ? 0 : Width / Height;
}
