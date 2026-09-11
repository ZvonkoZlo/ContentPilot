using ContentPilot.Domain.Quality;

namespace ContentPilot.Application.Quality.Checks;

/// <summary>
/// WCAG contrast against the pixels actually behind the text, which the renderer sampled at
/// render time. Sampling the real backdrop is what makes this trustworthy: a headline over a
/// photograph has no single background colour to reason about, and a design system that
/// only checks token-against-token passes it every time.
/// </summary>
internal static class ContrastCheck
{
    public static IEnumerable<QaFinding> Run(DeterministicQaInput input)
    {
        var options = input.Options;
        var scale = input.Report.DeviceScaleFactor <= 0 ? 1 : input.Report.DeviceScaleFactor;

        foreach (var slot in input.Report.Slots)
        {
            if (slot.ContrastRatio is not { } ratio || input.Manifest.FindTextSlot(slot.SlotId) is null)
            {
                continue;
            }

            // WCAG relaxes the ratio for large text because weight and size carry legibility
            // that contrast otherwise has to. Most headline slots qualify.
            var cssPx = slot.FontSizePx / scale;
            var isLarge = cssPx >= options.LargeTextCssPx;
            var floor = isLarge ? options.MinLargeTextContrastRatio : options.MinContrastRatio;

            if (ratio >= floor)
            {
                continue;
            }

            // Well below the floor is unreadable; just under it is a judgement call that a
            // designer would make differently on a photograph than on a flat ground.
            var severity = ratio < floor * 0.75 ? QaSeverity.Blocking : QaSeverity.Major;

            yield return QaFinding.Deterministic(
                QaFindingCode.LowContrast,
                severity,
                $"Slot '{slot.SlotId}' has a contrast ratio of {ratio:F2}:1 against its backdrop, " +
                $"below the {floor:F1}:1 floor for {(isLarge ? "large" : "body")} text.",
                slot.SlotId,
                ratio,
                floor);
        }
    }
}
