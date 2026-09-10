using ContentPilot.Rendering.Contracts;

namespace ContentPilot.Application.Quality;

/// <summary>
/// Everything gate 1 needs, and nothing it could fetch itself. Every field is a measurement
/// somebody else already made: the browser measured the boxes, the mask render measured the
/// occlusion, the comparer measured the fidelity. This gate does arithmetic and lookups
/// against the manifest — it never looks at a pixel.
/// </summary>
public sealed record DeterministicQaInput
{
    /// <summary>The single source of truth for budgets, occlusion allowances and safe areas.</summary>
    public required TemplateManifest Manifest { get; init; }

    public required AspectRatio AspectRatio { get; init; }

    public required RenderReport Report { get; init; }

    /// <summary>One entry per immutable asset slot that was verified. Empty is legitimate.</summary>
    public IReadOnlyList<CompareResult> Fidelity { get; init; } = [];

    /// <summary>
    /// The thresholds the comparison actually ran with, so the findings can name the number
    /// they tripped rather than a default that may not have been in force.
    /// </summary>
    public FidelityThresholds FidelityThresholds { get; init; } = FidelityThresholds.Default;

    /// <summary>
    /// Asset slot id to the source image's width/height ratio. A logo's rendered box is
    /// compared against this, which is how squashing is caught without image analysis.
    /// </summary>
    public IReadOnlyDictionary<string, double> SourceAspectRatios { get; init; } =
        new Dictionary<string, double>();

    /// <summary>
    /// The families the render asked for — normally the brand's heading and body fonts. A
    /// family that did not resolve falls back silently and produces a subtly wrong image
    /// that every later gate is likely to pass, so it is asserted rather than assumed.
    /// </summary>
    public IReadOnlyList<string> ExpectedFonts { get; init; } = [];

    /// <summary>Absent when only the report is being checked, as in a template smoke test.</summary>
    public RenderedFile? File { get; init; }

    public DeterministicQaOptions Options { get; init; } = new();
}

public sealed record RenderedFile
{
    public required int Width { get; init; }

    public required int Height { get; init; }

    public required long Bytes { get; init; }

    public ImageFormat Format { get; init; } = ImageFormat.Png;
}

/// <summary>
/// The numbers gate 1 judges against. They live here rather than as constants so the
/// calibration harness can move one and re-run the corpus, and so a test can state the
/// threshold it is exercising instead of restating a magic number.
/// </summary>
public sealed record DeterministicQaOptions
{
    /// <summary>WCAG AA for body text.</summary>
    public double MinContrastRatio { get; init; } = 4.5;

    /// <summary>WCAG AA for large text, which is most headline slots.</summary>
    public double MinLargeTextContrastRatio { get; init; } = 3.0;

    /// <summary>CSS pixels at 1x, above which WCAG treats text as large.</summary>
    public double LargeTextCssPx { get; init; } = 24;

    /// <summary>
    /// Fraction of a text slot another element may cover. Text is meant to be read, so this
    /// is tighter than any asset allowance — but not zero, because antialiased edges of an
    /// adjacent element routinely graze a box by a fraction of a percent.
    /// </summary>
    public double MaxTextOcclusion { get; init; } = 0.02;

    /// <summary>Rendered-versus-source aspect tolerance for a logo. The same 0.5% as §10.</summary>
    public double MaxLogoAspectDelta { get; init; } = 0.005;

    /// <summary>
    /// Below this a 1080-wide PNG is not a real render — it is a blank frame, or the
    /// screenshot never arrived. Cheap, and it catches an entire class of silent failure.
    /// </summary>
    public long MinPlausibleBytes { get; init; } = 20_000;

    /// <summary>Above this the file is too heavy to attach to an email or upload quickly.</summary>
    public long MaxPlausibleBytes { get; init; } = 8_000_000;

    /// <summary>
    /// Shrink-to-fit absorbing a near miss is the feature working. This many slots shrinking
    /// in one render means the copy budgets are wrong, which is worth saying out loud.
    /// </summary>
    public int MaxShrunkSlots { get; init; } = 1;
}
