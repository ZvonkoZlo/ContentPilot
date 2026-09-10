using ContentPilot.Domain.Quality;
using ContentPilot.Rendering.Contracts;

namespace ContentPilot.Application.Quality.Checks;

/// <summary>
/// Logo integrity: the one brand rule a client notices immediately and forgives slowly. All
/// three checks are geometry against the manifest and the source asset — no pixels, no
/// model.
/// </summary>
internal static class LogoChecks
{
    public static IEnumerable<QaFinding> Run(DeterministicQaInput input)
    {
        var measured = input.Report.Slots.ToDictionary(s => s.SlotId, StringComparer.Ordinal);

        foreach (var slot in input.Manifest.AssetSlots.Where(s => s.Kind == AssetKind.Logo))
        {
            if (!measured.TryGetValue(slot.Id, out var box))
            {
                continue;
            }

            foreach (var finding in Check(input, slot, box, measured))
            {
                yield return finding;
            }
        }
    }

    private static IEnumerable<QaFinding> Check(
        DeterministicQaInput input,
        AssetSlot slot,
        SlotMeasurement measurement,
        IReadOnlyDictionary<string, SlotMeasurement> allSlots)
    {
        if (input.SourceAspectRatios.TryGetValue(slot.Id, out var sourceAspect) &&
            sourceAspect > 0 &&
            measurement.Box.AspectRatio > 0)
        {
            var delta = Math.Abs(measurement.Box.AspectRatio - sourceAspect) / sourceAspect;

            if (delta > input.Options.MaxLogoAspectDelta)
            {
                yield return QaFinding.Deterministic(
                    QaFindingCode.LogoDistorted,
                    QaSeverity.Blocking,
                    $"Logo in '{slot.Id}' renders at {measurement.Box.AspectRatio:F3} against a source ratio of " +
                    $"{sourceAspect:F3} — a {delta:P1} distortion.",
                    slot.Id,
                    delta,
                    input.Options.MaxLogoAspectDelta);
            }
        }

        if (slot.MinWidthPx > 0 && measurement.Box.Width < slot.MinWidthPx)
        {
            yield return QaFinding.Deterministic(
                QaFindingCode.LogoTooSmall,
                QaSeverity.Major,
                $"Logo in '{slot.Id}' renders {measurement.Box.Width:F0} px wide, below the {slot.MinWidthPx} px legibility floor.",
                slot.Id,
                measurement.Box.Width,
                slot.MinWidthPx);
        }

        if (slot.ClearSpaceRatio <= 0)
        {
            yield break;
        }

        // Clear space is a rule about neighbours, and every neighbour's box is in the same
        // report — so this is an intersection test, not something a vision model needs to
        // eyeball.
        var margin = measurement.Box.Width * slot.ClearSpaceRatio;
        var intruder = allSlots.Values.FirstOrDefault(other =>
            !string.Equals(other.SlotId, slot.Id, StringComparison.Ordinal) &&
            Intersects(Expand(measurement.Box, margin), other.Box));

        if (intruder is not null)
        {
            yield return QaFinding.Deterministic(
                QaFindingCode.LogoClearSpaceViolation,
                QaSeverity.Minor,
                $"Slot '{intruder.SlotId}' sits inside the {slot.ClearSpaceRatio:P0} clear space around the logo.",
                slot.Id,
                margin);
        }
    }

    private static BoundingBox Expand(BoundingBox box, double margin) => new()
    {
        X = box.X - margin,
        Y = box.Y - margin,
        Width = box.Width + (margin * 2),
        Height = box.Height + (margin * 2),
    };

    private static bool Intersects(BoundingBox a, BoundingBox b) =>
        a.X < b.X + b.Width &&
        b.X < a.X + a.Width &&
        a.Y < b.Y + b.Height &&
        b.Y < a.Y + a.Height;
}
