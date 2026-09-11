using System.Text.Json.Serialization;

namespace ContentPilot.Rendering.Contracts;

/// <summary>
/// A complete, deterministic reel request. Every asset is carried as bytes so the renderer
/// can keep the same no-network guarantee as image rendering.
/// </summary>
public sealed record ReelSpec
{
    public required string TemplateId { get; init; }

    /// <summary>Pins the reel template version. Null means the currently installed version.</summary>
    public int? TemplateVersion { get; init; }

    public double DurationSeconds { get; init; } = 15;

    public required BrandTokens Brand { get; init; }

    public ColorScheme ColorScheme { get; init; } = ColorScheme.LightOnDark;

    public string Language { get; init; } = "en";

    public required IReadOnlyList<ReelSceneSpec> Scenes { get; init; }

    /// <summary>A licensed music bed supplied by the caller. The renderer never fetches audio.</summary>
    public BinaryPayload? Music { get; init; }

    public ReelRenderOptions Options { get; init; } = new();
}

public sealed record ReelSceneSpec
{
    /// <summary>Matches one scene id in the selected reel manifest.</summary>
    public required string SceneId { get; init; }

    /// <summary>
    /// Source duration before the following transition overlaps it. The final duration is
    /// the sum of scene durations minus transition overlaps.
    /// </summary>
    public required double DurationSeconds { get; init; }

    public required IReadOnlyDictionary<string, string> Text { get; init; }

    public IReadOnlyDictionary<string, ImagePayload> Assets { get; init; } =
        new Dictionary<string, ImagePayload>();

    /// <summary>Reserved for a future text-to-speech implementation; ignored by the MVP renderer.</summary>
    public string? Narration { get; init; }
}

public sealed record ReelRenderOptions
{
    public int FramesPerSecond { get; init; } = 30;

    public int ConstantRateFactor { get; init; } = 20;

    public double MusicVolume { get; init; } = 0.18;

    public bool IncludeKeyframes { get; init; } = true;

    public double DurationToleranceSeconds { get; init; } = 0.12;
}

/// <summary>Base64 bytes for media that is not an image, currently MP4 and supplied audio.</summary>
public sealed record BinaryPayload
{
    public required string MediaType { get; init; }

    public required string Base64 { get; init; }

    public static BinaryPayload FromBytes(ReadOnlySpan<byte> bytes, string mediaType) =>
        new() { MediaType = mediaType, Base64 = Convert.ToBase64String(bytes) };

    public byte[] ToBytes() => Convert.FromBase64String(Base64);
}

/// <summary>
/// Reel manifests keep scene order, timing, slot budgets and motion choices together. They
/// are the single source of truth used by validation, rendering and deterministic QA.
/// </summary>
public sealed record ReelTemplateManifest
{
    public required string TemplateId { get; init; }

    public required int Version { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public bool SafeMode { get; init; }

    public SafeAreas SafeAreas { get; init; } = SafeAreas.Default;

    public IReadOnlyList<ColorScheme> ColorSchemes { get; init; } = [ColorScheme.LightOnDark];

    public required IReadOnlyList<ReelSceneManifest> Scenes { get; init; }

    public ReelSceneManifest? FindScene(string id) =>
        Scenes.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));
}

public sealed record ReelSceneManifest
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required double DefaultDurationSeconds { get; init; }

    public double MinDurationSeconds { get; init; } = 3;

    public double MaxDurationSeconds { get; init; } = 5;

    /// <summary>Overlap with the next scene. Must be zero on the last scene.</summary>
    public double TransitionSeconds { get; init; } = 0.5;

    public ReelTransition Transition { get; init; } = ReelTransition.Fade;

    public ReelBackgroundMotion BackgroundMotion { get; init; } = ReelBackgroundMotion.PushIn;

    public ReelProductMotion ProductMotion { get; init; } = ReelProductMotion.None;

    public required IReadOnlyList<TextSlot> TextSlots { get; init; }

    public IReadOnlyList<AssetSlot> AssetSlots { get; init; } = [];

    public TextSlot? FindTextSlot(string id) =>
        TextSlots.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));

    public AssetSlot? FindAssetSlot(string id) =>
        AssetSlots.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReelTransition
{
    Fade,
    WipeLeft,
    SlideUp,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReelBackgroundMotion
{
    Still,
    PushIn,
    PullOut,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReelProductMotion
{
    None,
    Rise,
    SlideLeft,
    SlideRight,
}

public sealed record RenderReelResponse
{
    public required string TemplateId { get; init; }

    public required int TemplateVersion { get; init; }

    public required BinaryPayload Video { get; init; }

    public required ImagePayload Cover { get; init; }

    public required IReadOnlyList<ReelKeyframe> Keyframes { get; init; }

    public required ReelRenderReport Report { get; init; }
}

public sealed record ReelKeyframe
{
    public required double TimestampSeconds { get; init; }

    public required ReelKeyframeKind Kind { get; init; }

    public required ImagePayload Image { get; init; }

    public required double BlackPixelFraction { get; init; }

    public required ulong PerceptualHash { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReelKeyframeKind
{
    Cover,
    SceneMidpoint,
    TransitionStart,
    TransitionEnd,
}

public sealed record ReelRenderReport
{
    public required int Width { get; init; }

    public required int Height { get; init; }

    public required double DurationSeconds { get; init; }

    public required double FramesPerSecond { get; init; }

    public required bool HasAudio { get; init; }

    /// <summary>Maximum audio level in dBFS. Negative infinity is represented as null.</summary>
    public double? AudioPeakDbfs { get; init; }

    public required int BlackFrameCount { get; init; }

    public required long RenderDurationMs { get; init; }

    public required IReadOnlyList<ReelSceneRenderReport> Scenes { get; init; }

    public required IReadOnlyList<ReelCheckResult> Checks { get; init; }

    public bool Passed => Checks.All(c => c.Passed);
}

public sealed record ReelSceneRenderReport
{
    public required string SceneId { get; init; }

    public required double StartsAtSeconds { get; init; }

    public required double EndsAtSeconds { get; init; }

    public required RenderReport StaticReport { get; init; }
}

public sealed record ReelCheckResult
{
    public required ReelCheckCode Code { get; init; }

    public required bool Passed { get; init; }

    public required string Detail { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReelCheckCode
{
    Duration,
    FrameRate,
    Resolution,
    AudioTrack,
    AudioPeak,
    BlackFrames,
    TextReadability,
    StaticLayout,
}

/// <summary>
/// Swappable reel renderer boundary. The MVP implementation uses Playwright layers and
/// FFmpeg; a later motion renderer can implement the same contract.
/// </summary>
public interface IReelRenderer
{
    Task<RenderReelResponse> RenderAsync(ReelSpec spec, CancellationToken cancellationToken);
}

/// <summary>Future narration boundary. Phase 7 deliberately ships no implementation.</summary>
public interface ITextToSpeechClient
{
    Task<BinaryPayload> SynthesizeAsync(string narration, string language, CancellationToken cancellationToken);
}
