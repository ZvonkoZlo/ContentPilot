using ContentPilot.Application.Quality;
using ContentPilot.Domain.Quality;
using ContentPilot.Rendering.Contracts;
using Shouldly;
using ContractAssetKind = ContentPilot.Rendering.Contracts.AssetKind;

namespace ContentPilot.UnitTests.Quality;

/// <summary>
/// The gate-1 corpus: one clean render that must stay clean, and one injected defect per
/// check that must be caught. The two halves matter equally — a QA suite that fires on a
/// good render is worse than none, because it burns a remediation attempt and teaches
/// everyone to ignore findings.
/// </summary>
public sealed class DeterministicQaSuiteTests
{
    private static QaReport Run(DeterministicQaInput input) => DeterministicQaSuite.Run(input);

    [Fact]
    public void A_clean_render_produces_no_findings()
    {
        var report = Run(QaScenario.Clean());

        report.Findings.ShouldBeEmpty();
        report.Outcome.ShouldBe(QaOutcome.Pass);
        report.Score.ShouldBe(1.0);
        report.Gate.ShouldBe(QaGate.Deterministic);
    }

    [Fact]
    public void Clipped_text_fails_the_attempt()
    {
        var report = Run(QaScenario.Clean().With(QaScenario.Headline, s => s with { Overflows = true }));

        report.ShouldHave(QaFindingCode.TextOverflow, QaSeverity.Blocking);
        report.Outcome.ShouldBe(QaOutcome.Fail);
        report.Score.ShouldBe(0);
    }

    [Fact]
    public void Wrapping_past_the_line_budget_escalates_rather_than_fails()
    {
        // Five lines where the manifest budgeted three. It is not clipped, so it is not
        // unpublishable — but it is not what the template was designed around either.
        var report = Run(QaScenario.Clean().With(QaScenario.Headline, s => s with { LineCount = 5 }));

        report.ShouldHave(QaFindingCode.TooManyLines, QaSeverity.Major);
        report.Outcome.ShouldBe(QaOutcome.NeedsReview);
    }

    [Fact]
    public void Text_inside_the_platform_chrome_fails()
    {
        var report = Run(QaScenario.Clean().With(QaScenario.Headline, s => s with { BreaksSafeArea = true }));

        report.ShouldHave(QaFindingCode.SafeAreaViolation, QaSeverity.Blocking);
    }

    [Fact]
    public void A_background_that_reaches_the_edge_is_not_a_safe_area_violation()
    {
        // A background is supposed to bleed to the frame edge. Flagging it would train
        // everyone to ignore the check that catches a headline under the caption band.
        var input = QaScenario.Clean();
        var manifest = input.Manifest;

        input = input with
        {
            Manifest = manifest with
            {
                AssetSlots =
                [
                    .. manifest.AssetSlots,
                    new AssetSlot
                    {
                        Id = QaScenario.Background,
                        Kind = ContractAssetKind.Background,
                        Required = false,
                        MaxOcclusion = 1.0,
                    },
                ],
            },
            Report = input.Report with
            {
                Slots =
                [
                    .. input.Report.Slots,
                    QaScenario.Slot(QaScenario.Background, 0, 0, 1080, 1350) with { BreaksSafeArea = true },
                ],
            },
        };

        Run(input).Findings.ShouldNotContain(f => f.Code == QaFindingCode.SafeAreaViolation);
    }

    [Fact]
    public void Body_text_below_the_contrast_floor_is_caught()
    {
        // 16 CSS px is body text, so the 4.5:1 floor applies. 4.0 is under it but not
        // wildly so — the kind of call a designer might defend on a flat ground.
        var report = Run(QaScenario.Clean().With(QaScenario.Body, s => s with { ContrastRatio = 4.0 }));

        report.ShouldHave(QaFindingCode.LowContrast, QaSeverity.Major);
    }

    [Fact]
    public void Text_far_below_the_contrast_floor_fails_outright()
    {
        var report = Run(QaScenario.Clean().With(QaScenario.Body, s => s with { ContrastRatio = 2.0 }));

        report.ShouldHave(QaFindingCode.LowContrast, QaSeverity.Blocking);
    }

    [Fact]
    public void Large_headline_text_is_judged_against_the_relaxed_floor()
    {
        // 32 CSS px: WCAG large text, where 3:1 is the bar. The same ratio on body text
        // above is a finding, and that difference is the point of the check.
        var report = Run(QaScenario.Clean().With(QaScenario.Headline, s => s with { ContrastRatio = 3.4 }));

        report.Findings.ShouldNotContain(f => f.Code == QaFindingCode.LowContrast);
    }

    [Fact]
    public void Covered_text_fails()
    {
        var report = Run(QaScenario.Clean().With(QaScenario.Headline, s => s with { Occlusion = 0.10 }));

        report.ShouldHave(QaFindingCode.SlotOccluded, QaSeverity.Blocking);
    }

    [Fact]
    public void Repeated_shrink_to_fit_reports_the_budgets_without_failing_the_render()
    {
        // Shrink-to-fit working is not a defect in this image. Two slots needing it is a
        // defect in the copy brief, and it will keep costing renders until someone hears.
        var report = Run(QaScenario.Clean()
            .With(QaScenario.Headline, s => s with { ShrinkApplied = true })
            .With(QaScenario.Body, s => s with { ShrinkApplied = true }));

        report.ShouldHave(QaFindingCode.ShrinkToFitAbused, QaSeverity.Minor);
        report.Outcome.ShouldBe(QaOutcome.Pass);
        report.Score.ShouldBeLessThan(1.0);
    }

    [Fact]
    public void A_required_slot_that_never_rendered_fails()
    {
        var input = QaScenario.Clean();
        var report = Run(input.WithSlots([.. input.Report.Slots.Where(s => s.SlotId != QaScenario.Screenshot)]));

        report.ShouldHave(QaFindingCode.RequiredSlotMissing, QaSeverity.Blocking);
    }

    [Fact]
    public void A_squashed_logo_fails()
    {
        // 160x53 is a 3:1 box for a 4:1 logo. Nobody forgives a stretched wordmark.
        var report = Run(QaScenario.Clean().With(QaScenario.Logo, s => s with
        {
            Box = s.Box with { Height = 53.3 },
        }));

        report.ShouldHave(QaFindingCode.LogoDistorted, QaSeverity.Blocking);
    }

    [Fact]
    public void A_logo_below_the_legibility_floor_is_caught()
    {
        var report = Run(QaScenario.Clean().With(QaScenario.Logo, s => s with
        {
            Box = new BoundingBox { X = 60, Y = 1200, Width = 80, Height = 20 },
        }));

        report.ShouldHave(QaFindingCode.LogoTooSmall, QaSeverity.Major);
    }

    [Fact]
    public void Something_inside_the_logo_clear_space_is_reported()
    {
        // The body slot pushed down to overlap the 25% margin around the logo.
        var report = Run(QaScenario.Clean().With(QaScenario.Body, s => s with
        {
            Box = new BoundingBox { X = 60, Y = 1170, Width = 400, Height = 20 },
        }));

        report.ShouldHave(QaFindingCode.LogoClearSpaceViolation, QaSeverity.Minor);
    }

    [Fact]
    public void An_overlay_on_the_product_ui_fails_with_the_occlusion_code()
    {
        var report = Run(QaScenario.Clean() with
        {
            Fidelity = [QaScenario.Fidelity(FidelityVerdict.Fail, occlusion: 0.18)],
        });

        report.ShouldHave(QaFindingCode.ScreenshotOccluded, QaSeverity.Blocking);
    }

    [Fact]
    public void The_wrong_picture_in_the_slot_is_reported_once_and_precisely()
    {
        // Every structural and colour metric is meaningless against a reference this image
        // has nothing to do with, so reporting a blur as well would be noise.
        var report = Run(QaScenario.Clean() with
        {
            Fidelity = [QaScenario.Fidelity(FidelityVerdict.Fail, phash: 36, ssim: 0.0, deltaE: 100)],
        });

        report.ShouldHave(QaFindingCode.ScreenshotWrongAsset, QaSeverity.Blocking);
        report.Findings.ShouldNotContain(f => f.Code == QaFindingCode.ScreenshotAltered);
        report.Findings.ShouldNotContain(f => f.Code == QaFindingCode.ScreenshotTinted);
    }

    [Fact]
    public void A_scrim_over_the_product_is_reported_as_a_tint_not_a_structural_change()
    {
        // The measured signature of the classic scrim mistake: structure intact, colour
        // shifted. Similarity alone would wave it through.
        var report = Run(QaScenario.Clean() with
        {
            Fidelity = [QaScenario.Fidelity(FidelityVerdict.Fail, ssim: 0.9824, deltaE: 12.63)],
        });

        report.ShouldHave(QaFindingCode.ScreenshotTinted, QaSeverity.Blocking);
        report.Findings.ShouldNotContain(f => f.Code == QaFindingCode.ScreenshotAltered);
    }

    [Fact]
    public void The_review_band_escalates_instead_of_failing()
    {
        var report = Run(QaScenario.Clean() with
        {
            Fidelity = [QaScenario.Fidelity(FidelityVerdict.NeedsVisualReview, ssim: 0.9362)],
        });

        report.ShouldHave(QaFindingCode.ScreenshotAltered, QaSeverity.Major);
        report.Outcome.ShouldBe(QaOutcome.NeedsReview);
    }

    [Fact]
    public void A_non_pass_verdict_is_never_swallowed_even_when_no_metric_explains_it()
    {
        // A verdict the mapping does not model must still be a finding. Silence here would
        // turn a comparer change into a QA hole nobody notices.
        var report = Run(QaScenario.Clean() with
        {
            Fidelity = [QaScenario.Fidelity(FidelityVerdict.Fail, reasons: "reserved for a future metric")],
        });

        report.Outcome.ShouldBe(QaOutcome.Fail);
        report.Findings.ShouldContain(f => f.Code == QaFindingCode.ScreenshotAltered);
    }

    [Fact]
    public void A_frame_rendered_at_the_wrong_size_fails()
    {
        var input = QaScenario.Clean();
        var report = Run(input with
        {
            Report = input.Report with { Width = 1080, Height = 1080 },
            File = new RenderedFile { Width = 1080, Height = 1080, Bytes = 400_000 },
        });

        report.ShouldHave(QaFindingCode.WrongDimensions, QaSeverity.Blocking);
    }

    [Fact]
    public void A_blank_frame_is_caught_by_its_byte_size()
    {
        var report = Run(QaScenario.Clean() with
        {
            File = new RenderedFile { Width = 1080, Height = 1350, Bytes = 3_142 },
        });

        report.ShouldHave(QaFindingCode.ImplausibleFileSize, QaSeverity.Blocking);
    }

    [Fact]
    public void A_font_that_did_not_resolve_is_reported()
    {
        var input = QaScenario.Clean();
        var report = Run(input with
        {
            Report = input.Report with { FontsLoaded = ["Inter"] },
        });

        report.ShouldHave(QaFindingCode.FontFallback, QaSeverity.Major);
    }

    [Fact]
    public void A_template_reaching_for_the_network_is_reported()
    {
        var input = QaScenario.Clean();
        var report = Run(input with
        {
            Report = input.Report with { BlockedRequests = ["https://fonts.googleapis.com/css2"] },
        });

        report.ShouldHave(QaFindingCode.BlockedNetworkRequest, QaSeverity.Major);
    }

    [Fact]
    public void Every_check_runs_even_after_the_first_blocking_defect()
    {
        // The remediation router decides what to do from the whole picture of an attempt.
        // An early exit would hand it one symptom and hide the rest.
        var report = Run(QaScenario.Clean()
            .With(QaScenario.Headline, s => s with { Overflows = true })
            .With(QaScenario.Body, s => s with { ContrastRatio = 2.0 })
            .With(QaScenario.Logo, s => s with { Box = s.Box with { Height = 53.3 } }));

        report.Findings.Select(f => f.Code).ShouldBe(
            [QaFindingCode.TextOverflow, QaFindingCode.LowContrast, QaFindingCode.LogoDistorted],
            ignoreOrder: true);
    }

    [Fact]
    public void Findings_are_ordered_worst_first()
    {
        var report = Run(QaScenario.Clean()
            .With(QaScenario.Headline, s => s with { Overflows = true, ShrinkApplied = true })
            .With(QaScenario.Body, s => s with { LineCount = 9, ShrinkApplied = true }));

        report.Findings.Select(f => f.Severity)
            .ShouldBe(report.Findings.Select(f => f.Severity).OrderByDescending(s => s));
    }

    [Fact]
    public void Every_finding_the_suite_produces_belongs_to_the_deterministic_gate()
    {
        // The gate contract in one assertion: a model gate must never be able to claim a
        // measurement finding, and this gate must never claim a judgement one.
        var report = Run(QaScenario.Clean()
            .With(QaScenario.Headline, s => s with { Overflows = true, BreaksSafeArea = true, Occlusion = 0.4 })
            .With(QaScenario.Body, s => s with { ContrastRatio = 1.2, LineCount = 12 }));

        report.Findings.ShouldNotBeEmpty();
        report.Findings.ShouldAllBe(f => QaFindingCodes.GateFor(f.Code) == QaGate.Deterministic);

        // And it survives being written down, which is where that contract is enforced.
        Should.NotThrow(() => report.ToReview(Guid.CreateVersion7(), Guid.CreateVersion7(), 1, DateTimeOffset.UtcNow));
    }
}

internal static class QaReportAssertions
{
    public static void ShouldHave(this QaReport report, QaFindingCode code, QaSeverity severity)
    {
        var finding = report.Findings.FirstOrDefault(f => f.Code == code);

        finding.ShouldNotBeNull(
            $"Expected {code}, got: {string.Join(", ", report.Findings.Select(f => $"{f.Code} ({f.Severity})"))}");

        finding.Severity.ShouldBe(severity, $"{code}: {finding.Detail}");
    }
}
