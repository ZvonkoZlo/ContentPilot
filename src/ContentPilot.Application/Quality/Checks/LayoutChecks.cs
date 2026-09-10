using ContentPilot.Domain.Quality;
using ContentPilot.Rendering.Contracts;

namespace ContentPilot.Application.Quality.Checks;

/// <summary>
/// Typography and placement, read straight out of the render report. Zero false positives
/// by construction: the layout engine already knows whether a box overflowed, so this is a
/// lookup rather than an inference from pixels.
/// </summary>
internal static class LayoutChecks
{
    public static IEnumerable<QaFinding> Run(DeterministicQaInput input)
    {
        var manifest = input.Manifest;
        var options = input.Options;
        var measured = input.Report.Slots.ToDictionary(s => s.SlotId, StringComparer.Ordinal);

        foreach (var slot in manifest.TextSlots.Where(s => s.Required))
        {
            if (!measured.ContainsKey(slot.Id))
            {
                yield return QaFinding.Deterministic(
                    QaFindingCode.RequiredSlotMissing,
                    QaSeverity.Blocking,
                    $"Required text slot '{slot.Id}' ({slot.Role}) was not rendered.",
                    slot.Id);
            }
        }

        foreach (var assetSlot in manifest.AssetSlots.Where(s => s.Required))
        {
            if (!measured.ContainsKey(assetSlot.Id))
            {
                yield return QaFinding.Deterministic(
                    QaFindingCode.RequiredSlotMissing,
                    QaSeverity.Blocking,
                    $"Required asset slot '{assetSlot.Id}' ({assetSlot.Kind}) was not rendered.",
                    assetSlot.Id);
            }
        }

        var shrunk = 0;

        foreach (var slot in input.Report.Slots)
        {
            var textSlot = manifest.FindTextSlot(slot.SlotId);
            var assetSlot = manifest.FindAssetSlot(slot.SlotId);

            if (slot.Overflows)
            {
                // Clipped text is the single most common template failure and the cheapest
                // to catch. Blocking, always: a cut-off headline is not publishable.
                yield return QaFinding.Deterministic(
                    QaFindingCode.TextOverflow,
                    QaSeverity.Blocking,
                    $"Slot '{slot.SlotId}' is clipped: its content does not fit its box.",
                    slot.SlotId);
            }

            if (textSlot is not null && slot.LineCount > textSlot.MaxLines)
            {
                yield return QaFinding.Deterministic(
                    QaFindingCode.TooManyLines,
                    QaSeverity.Major,
                    $"Slot '{slot.SlotId}' wrapped to {slot.LineCount} lines against a budget of {textSlot.MaxLines}.",
                    slot.SlotId,
                    slot.LineCount,
                    textSlot.MaxLines);
            }

            if (slot.ShrinkApplied)
            {
                shrunk++;
            }

            if (slot.BreaksSafeArea && MustStayClearOfChrome(textSlot, assetSlot))
            {
                // Platform chrome sits on top of the frame. Anything that must be read
                // inside those margins is read by nobody.
                yield return QaFinding.Deterministic(
                    QaFindingCode.SafeAreaViolation,
                    QaSeverity.Blocking,
                    $"Slot '{slot.SlotId}' extends into the platform safe-area margin.",
                    slot.SlotId);
            }

            if (textSlot is not null && slot.Occlusion > options.MaxTextOcclusion)
            {
                yield return QaFinding.Deterministic(
                    QaFindingCode.SlotOccluded,
                    QaSeverity.Blocking,
                    $"Slot '{slot.SlotId}' is {slot.Occlusion:P1} covered by later-painted elements.",
                    slot.SlotId,
                    slot.Occlusion,
                    options.MaxTextOcclusion);
            }

            // Immutable slots get the full fidelity treatment; this covers the rest, where
            // the manifest still says how much may be painted over the image.
            if (assetSlot is { Immutable: false } && slot.Occlusion > assetSlot.MaxOcclusion)
            {
                yield return QaFinding.Deterministic(
                    QaFindingCode.SlotOccluded,
                    QaSeverity.Major,
                    $"Asset slot '{slot.SlotId}' is {slot.Occlusion:P1} covered against an allowance of {assetSlot.MaxOcclusion:P1}.",
                    slot.SlotId,
                    slot.Occlusion,
                    assetSlot.MaxOcclusion);
            }
        }

        if (shrunk > options.MaxShrunkSlots)
        {
            // Not a defect in this image — shrink-to-fit did its job. It is a defect in the
            // copy budgets, and it will keep costing renders until someone hears about it.
            yield return QaFinding.Deterministic(
                QaFindingCode.ShrinkToFitAbused,
                QaSeverity.Minor,
                $"{shrunk} slots needed shrink-to-fit; the copy budgets for '{manifest.TemplateId}' are too generous.",
                measured: shrunk,
                threshold: options.MaxShrunkSlots);
        }
    }

    /// <summary>
    /// Text and logos must clear the chrome. A background legitimately fills the frame, and
    /// flagging it would train everyone to ignore the check.
    /// </summary>
    private static bool MustStayClearOfChrome(TextSlot? textSlot, AssetSlot? assetSlot) =>
        textSlot is not null || assetSlot?.Kind is AssetKind.Logo;
}
