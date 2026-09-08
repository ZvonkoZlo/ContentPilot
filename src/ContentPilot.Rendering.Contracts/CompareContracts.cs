using System.Text.Json.Serialization;

namespace ContentPilot.Rendering.Contracts;

/// <summary>
/// Screenshot fidelity verification. The strong guarantee comes first — no model ever
/// touches these pixels, and the spec validator refuses a generated asset in an immutable
/// slot — so this is a regression check against renderer bugs: a stray overlay, a wrong
/// crop, an unintended opacity, a gradient scrim that swallows the UI.
/// </summary>
public sealed record CompareRequest
{
    /// <summary>The immutable source asset, at its original size.</summary>
    public required ImagePayload Reference { get; init; }

    /// <summary>The full render the slot was composited into.</summary>
    public required ImagePayload Rendered { get; init; }

    /// <summary>
    /// The mask render for this slot. It yields the slot's exact device-pixel box and its
    /// occlusion without any image analysis — the browser already knows both.
    /// </summary>
    public ImagePayload? Mask { get; init; }

    /// <summary>Used only when no mask is supplied.</summary>
    public BoundingBox? SlotBox { get; init; }

    public required string SlotId { get; init; }

    public FidelityThresholds Thresholds { get; init; } = FidelityThresholds.Default;
}

/// <summary>
/// Calibrated against the Phase 2 mutation corpus rather than guessed. Measured on a
/// synthetic 828x1792 product screenshot rendered into a ~300x600 slot — a 2.8x downscale,
/// which is the hard case:
/// <code>
///   case          ssim     dE      phash
///   clean         0.9872   0.24    6
///   jpeg q82      0.9541   0.44    6
///   jpeg q40      0.9288   0.72    4
///   10% scrim     0.9824   5.51    6
///   22% tint      0.9799   12.63   6
///   35% scrim     0.8920   20.86   6
///   blur sigma 1  0.9362   0.31    6
///   blur sigma 3  0.8392   0.60    6
///   wrong image   0.0000   100.0   36
/// </code>
/// Two things the table makes obvious: structural similarity is nearly blind to a tint or
/// a light scrim, and colour difference is nearly blind to a blur. Neither metric alone
/// would do, which is why both gate the verdict.
/// <para>
/// The band is tighter than it looks. Feeding a poster-like image (large type on a
/// gradient) through the same slot measures 0.9491 while being perfectly intact — just
/// under the pass line — because the calibration set was dense UI, not sparse artwork.
/// Content unlike the calibration set lands in the review band rather than passing.
/// </para>
/// <para>
/// Phase 4 re-runs this against real Appointso screenshots at every supported scale and
/// re-derives these numbers, and should widen the corpus beyond one image type. Until
/// then they are honest but narrow.
/// </para>
/// </summary>
public sealed record FidelityThresholds
{
    public static readonly FidelityThresholds Default = new();

    /// <summary>Fraction of the slot other elements may cover. Exact, not a heuristic.</summary>
    public double MaxOcclusion { get; init; } = 0.02;

    /// <summary>Squashing and stretching. The source may be scaled, never distorted.</summary>
    public double MaxAspectDelta { get; init; } = 0.005;

    /// <summary>Above this Hamming distance it is not even the same picture.</summary>
    public int MaxPerceptualHashDistance { get; init; } = 12;

    /// <summary>
    /// At or above this the render is clean. Set below the clean measurement but above
    /// ordinary delivery compression, so enabling JPEG output does not start failing
    /// every item.
    /// </summary>
    public double PassStructuralSimilarity { get; init; } = 0.95;

    /// <summary>Below this it is a failure with no second opinion.</summary>
    public double FailStructuralSimilarity { get; init; } = 0.90;

    /// <summary>
    /// Structural similarity on luminance is nearly blind to a uniform colour cast, which
    /// is exactly what an opacity overlay or a wrong colour profile produces.
    /// </summary>
    public double PassMeanDeltaE { get; init; } = 2.0;

    public double FailMeanDeltaE { get; init; } = 5.0;
}

public sealed record CompareResult
{
    public required string SlotId { get; init; }

    public required FidelityVerdict Verdict { get; init; }

    /// <summary>Populated when the verdict is not a pass, in severity order.</summary>
    public required IReadOnlyList<string> Reasons { get; init; }

    public required FidelityMetrics Metrics { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FidelityVerdict
{
    /// <summary>Clean. No model is asked to look at it.</summary>
    Pass,

    /// <summary>
    /// The grey zone. A vision model is shown the crop beside the reference and asked one
    /// narrow question: is the product UI altered?
    /// </summary>
    NeedsVisualReview,

    /// <summary>Deterministic failure. Remediation is routed without spending a token.</summary>
    Fail,
}

public sealed record FidelityMetrics
{
    public required double Occlusion { get; init; }

    public required double AspectDelta { get; init; }

    public required int PerceptualHashDistance { get; init; }

    /// <summary>Multi-scale structural similarity on luminance, 0 to 1.</summary>
    public required double StructuralSimilarity { get; init; }

    /// <summary>Mean CIEDE2000 colour difference over a coarse grid.</summary>
    public required double MeanDeltaE { get; init; }

    /// <summary>The slot's device-pixel box, as measured from the mask.</summary>
    public BoundingBox? MeasuredBox { get; init; }
}
