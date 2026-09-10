using ContentPilot.Domain.Quality;
using ContentPilot.Rendering.Contracts;

namespace ContentPilot.Application.Quality.Checks;

/// <summary>
/// Turns a fidelity verdict into findings. The comparison already decided pass, escalate or
/// fail; what is added here is <em>which</em> defect it was, because the remediation router
/// treats a wrong asset (re-fetch it) and a scrim over the UI (re-render without the
/// overlay) as different problems with different fixes.
/// <para>
/// The mapping reads the metrics rather than the verdict's reason strings on purpose: the
/// strings are for humans, and parsing prose to decide control flow is how a closed enum
/// quietly stops being closed.
/// </para>
/// </summary>
internal static class FidelityChecks
{
    public static IEnumerable<QaFinding> Run(DeterministicQaInput input)
    {
        foreach (var result in input.Fidelity)
        {
            if (result.Verdict == FidelityVerdict.Pass)
            {
                continue;
            }

            var thresholds = input.FidelityThresholds;
            var metrics = result.Metrics;

            // Manifest first: a slot may be stricter than the global default, and the
            // manifest is the source of truth for what this template allows.
            var maxOcclusion = input.Manifest.FindAssetSlot(result.SlotId) is { } slot && slot.Immutable
                ? slot.MaxOcclusion
                : thresholds.MaxOcclusion;

            var findings = Map(result, metrics, thresholds, maxOcclusion).ToList();

            if (findings.Count > 0)
            {
                foreach (var finding in findings)
                {
                    yield return finding;
                }

                continue;
            }

            // The comparer failed it on something this mapping does not model. Never
            // swallow that: an unexplained fail is still a fail.
            yield return QaFinding.Deterministic(
                QaFindingCode.ScreenshotAltered,
                Severity(result.Verdict),
                $"Fidelity check on '{result.SlotId}' returned {result.Verdict}: {string.Join("; ", result.Reasons)}",
                result.SlotId,
                metrics.StructuralSimilarity,
                thresholds.PassStructuralSimilarity);
        }
    }

    private static IEnumerable<QaFinding> Map(
        CompareResult result,
        FidelityMetrics metrics,
        FidelityThresholds thresholds,
        double maxOcclusion)
    {
        // Ordered by how certain each metric is. Occlusion is exact, so it is asked first;
        // a wrong picture makes every other number meaningless, so it is asked second.
        if (metrics.Occlusion > maxOcclusion)
        {
            yield return QaFinding.Deterministic(
                QaFindingCode.ScreenshotOccluded,
                QaSeverity.Blocking,
                $"{metrics.Occlusion:P1} of the product UI in '{result.SlotId}' is covered, against an allowance of {maxOcclusion:P1}.",
                result.SlotId,
                metrics.Occlusion,
                maxOcclusion);
        }

        if (metrics.PerceptualHashDistance > thresholds.MaxPerceptualHashDistance)
        {
            yield return QaFinding.Deterministic(
                QaFindingCode.ScreenshotWrongAsset,
                QaSeverity.Blocking,
                $"The image in '{result.SlotId}' is not the source asset (perceptual distance {metrics.PerceptualHashDistance}).",
                result.SlotId,
                metrics.PerceptualHashDistance,
                thresholds.MaxPerceptualHashDistance);

            // Everything below compares against a reference this image has nothing to do
            // with. Reporting a blur as well would just be noise.
            yield break;
        }

        if (metrics.AspectDelta > thresholds.MaxAspectDelta)
        {
            yield return QaFinding.Deterministic(
                QaFindingCode.ScreenshotDistorted,
                QaSeverity.Blocking,
                $"The screenshot in '{result.SlotId}' is scaled non-uniformly ({metrics.AspectDelta:P2} aspect drift).",
                result.SlotId,
                metrics.AspectDelta,
                thresholds.MaxAspectDelta);
        }

        if (metrics.StructuralSimilarity < thresholds.PassStructuralSimilarity)
        {
            var blocking = metrics.StructuralSimilarity < thresholds.FailStructuralSimilarity;

            yield return QaFinding.Deterministic(
                QaFindingCode.ScreenshotAltered,
                blocking ? QaSeverity.Blocking : QaSeverity.Major,
                $"The product UI in '{result.SlotId}' is structurally changed " +
                $"(similarity {metrics.StructuralSimilarity:F3} against a {thresholds.PassStructuralSimilarity:F2} pass line): a blur, a warp, or a wrong crop.",
                result.SlotId,
                metrics.StructuralSimilarity,
                thresholds.PassStructuralSimilarity);
        }

        if (metrics.MeanDeltaE > thresholds.PassMeanDeltaE)
        {
            var blocking = metrics.MeanDeltaE > thresholds.FailMeanDeltaE;

            // Structural similarity is nearly blind to a uniform cast, which is exactly what
            // a scrim or an opacity overlay produces. This is the metric that sees it.
            yield return QaFinding.Deterministic(
                QaFindingCode.ScreenshotTinted,
                blocking ? QaSeverity.Blocking : QaSeverity.Major,
                $"The product UI in '{result.SlotId}' carries a colour cast (mean ΔE {metrics.MeanDeltaE:F2}): an overlay, a scrim, or a wrong colour profile.",
                result.SlotId,
                metrics.MeanDeltaE,
                thresholds.PassMeanDeltaE);
        }
    }

    private static QaSeverity Severity(FidelityVerdict verdict) =>
        verdict == FidelityVerdict.Fail ? QaSeverity.Blocking : QaSeverity.Major;
}
