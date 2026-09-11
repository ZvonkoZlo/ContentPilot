using ContentPilot.Application.Orchestration;
using ContentPilot.Domain.Content;
using ContentPilot.Domain.Quality;
using Shouldly;

namespace ContentPilot.UnitTests.Orchestration;

/// <summary>
/// The self-correction mechanism from §8: where a finding routes, and how the escalation
/// ladder converges to a human rather than looping forever.
/// </summary>
public sealed class RemediationRouterTests
{
    private static QaFinding Finding(QaFindingCode code, QaSeverity severity = QaSeverity.Blocking) =>
        new() { Code = code, Severity = severity, Detail = code.ToString() };

    private static QaFinding Finding(QaFindingCode code, QaSeverity severity, double confidence) =>
        new() { Code = code, Severity = severity, Confidence = confidence, Detail = code.ToString() };

    [Fact]
    public void Every_finding_code_routes_somewhere()
    {
        // The static constructor already asserts this at process start; touching the type
        // here is what makes that assertion actually run inside the test suite.
        Should.NotThrow(() => RemediationRouter.Decide(
            Finding(QaFindingCode.TextOverflow), ContentItemStatus.Validating, 0, 3));
    }

    [Fact]
    public void The_worst_finding_is_the_one_acted_on()
    {
        var findings = new[]
        {
            Finding(QaFindingCode.ShrinkToFitAbused, QaSeverity.Minor),
            Finding(QaFindingCode.TextOverflow, QaSeverity.Blocking),
            Finding(QaFindingCode.TooManyLines, QaSeverity.Major),
        };

        RemediationRouter.PrimaryFinding(findings)!.Code.ShouldBe(QaFindingCode.TextOverflow);
    }

    [Fact]
    public void A_low_confidence_finding_from_a_model_gate_never_drives_the_decision()
    {
        // §9: "low-confidence findings are recorded but do not trigger remediation." A
        // vision model asked "is anything wrong?" will otherwise always find something.
        var findings = new[] { Finding(QaFindingCode.OffBrand, QaSeverity.Blocking, confidence: 0.4) };

        RemediationRouter.PrimaryFinding(findings).ShouldBeNull();
    }

    [Fact]
    public void A_finding_right_at_the_confidence_threshold_still_counts()
    {
        var findings = new[]
        {
            Finding(QaFindingCode.OffBrand, QaSeverity.Blocking, confidence: RemediationRouter.MinConfidenceForRemediation),
        };

        RemediationRouter.PrimaryFinding(findings)!.Code.ShouldBe(QaFindingCode.OffBrand);
    }

    [Fact]
    public void A_deterministic_finding_carries_no_confidence_and_is_never_filtered_by_it()
    {
        // A bounding box is not 80% sure — deterministic findings simply have no confidence
        // value, and the filter has to treat that as "always acts on it," not "never does."
        var findings = new[] { Finding(QaFindingCode.TextOverflow, QaSeverity.Blocking) };

        findings[0].Confidence.ShouldBeNull();
        RemediationRouter.PrimaryFinding(findings)!.Code.ShouldBe(QaFindingCode.TextOverflow);
    }

    [Fact]
    public void A_low_confidence_finding_is_skipped_in_favour_of_a_confident_lesser_one()
    {
        var findings = new[]
        {
            Finding(QaFindingCode.OffBrand, QaSeverity.Blocking, confidence: 0.2),
            Finding(QaFindingCode.WeakHook, QaSeverity.Major, confidence: 0.9),
        };

        RemediationRouter.PrimaryFinding(findings)!.Code.ShouldBe(QaFindingCode.WeakHook);
    }

    [Fact]
    public void A_minor_finding_alone_is_not_worth_an_attempt()
    {
        var findings = new[] { Finding(QaFindingCode.ShrinkToFitAbused, QaSeverity.Minor) };

        RemediationRouter.PrimaryFinding(findings).ShouldBeNull();
    }

    [Fact]
    public void Clipped_text_restarts_at_writing()
    {
        var decision = RemediationRouter.Decide(
            Finding(QaFindingCode.TextOverflow), ContentItemStatus.Validating, 0, 3);

        decision.Outcome.ShouldBe(RemediationOutcome.Restart);
        decision.RestartAt.ShouldBe(ContentItemStatus.Writing);
        decision.Rung.ShouldBe(1);
    }

    [Fact]
    public void A_wrong_screenshot_asset_is_a_spec_bug_not_a_copy_bug()
    {
        var decision = RemediationRouter.Decide(
            Finding(QaFindingCode.ScreenshotWrongAsset), ContentItemStatus.Validating, 0, 3);

        decision.RestartAt.ShouldBe(ContentItemStatus.SpecAssembly);
    }

    [Fact]
    public void The_first_two_rungs_target_the_codes_own_step()
    {
        var first = RemediationRouter.Decide(Finding(QaFindingCode.LowContrast), ContentItemStatus.Validating, 0, 3);
        var second = RemediationRouter.Decide(Finding(QaFindingCode.LowContrast), ContentItemStatus.Validating, 1, 3);

        first.Outcome.ShouldBe(RemediationOutcome.Restart);
        first.Rung.ShouldBe(1);
        second.Outcome.ShouldBe(RemediationOutcome.Restart);
        second.Rung.ShouldBe(2);
        second.RestartAt.ShouldBe(first.RestartAt);
    }

    [Fact]
    public void The_ladders_last_rung_falls_back_to_safe_mode_regardless_of_the_finding()
    {
        // Attempt 3 of 3: the plan's own fallback, not a code-specific fix.
        var decision = RemediationRouter.Decide(
            Finding(QaFindingCode.ScreenshotTinted), ContentItemStatus.Validating, 2, 3);

        decision.Outcome.ShouldBe(RemediationOutcome.SafeModeFallback);
        decision.RestartAt.ShouldBe(ContentItemStatus.Directing);
    }

    [Fact]
    public void Past_the_ladder_the_item_goes_to_human_review()
    {
        var decision = RemediationRouter.Decide(
            Finding(QaFindingCode.LowContrast), ContentItemStatus.Validating, 3, 3);

        decision.Outcome.ShouldBe(RemediationOutcome.NeedsHumanReview);
        decision.RestartAt.ShouldBeNull();
    }

    [Fact]
    public void A_maximum_of_one_quality_attempt_still_reaches_safe_mode_before_human_review()
    {
        // With the ladder shrunk to one rung, that rung is still SafeMode, not the code's
        // own target — the fallback is what "almost always renders cleanly" on the last try.
        var decision = RemediationRouter.Decide(
            Finding(QaFindingCode.LowContrast), ContentItemStatus.Validating, 0, 1);

        decision.Outcome.ShouldBe(RemediationOutcome.SafeModeFallback);
    }

    [Fact]
    public void The_same_fix_tried_twice_in_a_row_escalates_a_rung_immediately()
    {
        var decision = RemediationRouter.Decide(
            Finding(QaFindingCode.LowContrast),
            ContentItemStatus.Validating,
            qualityAttemptsSoFar: 0,
            maxQualityAttempts: 3,
            previous: (QaFindingCode.LowContrast, ContentItemStatus.SpecAssembly));

        // Attempt 1 would normally be rung 1; the repeat pushes it straight to rung 2.
        decision.Rung.ShouldBe(2);
    }

    [Fact]
    public void A_different_fix_does_not_trigger_the_repeat_escalation()
    {
        var decision = RemediationRouter.Decide(
            Finding(QaFindingCode.LowContrast),
            ContentItemStatus.Validating,
            qualityAttemptsSoFar: 0,
            maxQualityAttempts: 3,
            previous: (QaFindingCode.TextOverflow, ContentItemStatus.Writing));

        decision.Rung.ShouldBe(1);
    }

    [Fact]
    public void Repetitive_content_always_escalates_to_a_replan_regardless_of_attempt()
    {
        // Restarting at Directing with the same topic would just reproduce the repetition;
        // only the strategist banning the topic can actually fix it.
        var first = RemediationRouter.Decide(Finding(QaFindingCode.RepetitiveContent), ContentItemStatus.Validating, 0, 3);
        var later = RemediationRouter.Decide(Finding(QaFindingCode.RepetitiveContent), ContentItemStatus.Validating, 1, 3);

        first.Outcome.ShouldBe(RemediationOutcome.Replan);
        later.Outcome.ShouldBe(RemediationOutcome.Replan);
    }

    [Fact]
    public void A_target_later_than_the_source_step_falls_back_to_safe_mode_instead_of_cycling()
    {
        // LogoDistorted's base target is Rendering. Raised earlier, at Directing, that
        // target would sit later than the step being remediated — a cycle with no state
        // change — so the router refuses it and falls back rather than produce one.
        var decision = RemediationRouter.Decide(
            Finding(QaFindingCode.LogoDistorted), ContentItemStatus.Directing, 0, 3);

        decision.Outcome.ShouldBe(RemediationOutcome.SafeModeFallback);
    }

    [Fact]
    public void Every_base_target_is_a_legal_remediation_target_of_itself()
    {
        // Sanity check on the whole table: nothing routes to a step it cannot legally reach
        // when raised at that same step (the ordinary case: validating catches everything).
        foreach (var code in Enum.GetValues<QaFindingCode>())
        {
            var decision = RemediationRouter.Decide(Finding(code), ContentItemStatus.Validating, 0, 3);

            decision.Outcome.ShouldBeOneOf(RemediationOutcome.Restart, RemediationOutcome.Replan);
        }
    }
}
