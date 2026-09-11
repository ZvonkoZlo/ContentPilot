using ContentPilot.Application.Quality;
using ContentPilot.Domain.Content;
using Shouldly;

namespace ContentPilot.UnitTests.Quality;

/// <summary>
/// §9's pass-rate metric, exercised as a pure function — no campaign, no database, just the
/// shape of item outcomes the calculator has to reason about.
/// </summary>
public sealed class QaPassRateCalculatorTests
{
    private static QaPassRateItem Item(ContentItemStatus status, int attempts = 1) =>
        new() { Status = status, QualityAttempts = attempts };

    [Fact]
    public void An_item_approved_on_its_first_attempt_counts_as_a_first_attempt_pass()
    {
        var report = QaPassRateCalculator.Calculate([Item(ContentItemStatus.Approved, attempts: 1)]);

        report.FirstAttemptPasses.ShouldBe(1);
        report.FirstAttemptPassRate.ShouldBe(1.0);
    }

    [Fact]
    public void An_item_approved_only_after_remediation_does_not_count_as_a_first_attempt_pass()
    {
        var report = QaPassRateCalculator.Calculate([Item(ContentItemStatus.Approved, attempts: 2)]);

        report.Approved.ShouldBe(1);
        report.FirstAttemptPasses.ShouldBe(0);
        report.FirstAttemptPassRate.ShouldBe(0.0);
    }

    [Fact]
    public void An_item_still_in_flight_is_excluded_from_the_denominator_entirely()
    {
        // Neither a pass nor a miss yet — counting it as a miss would understate the rate
        // for a campaign that simply has not finished.
        var report = QaPassRateCalculator.Calculate([
            Item(ContentItemStatus.Approved, attempts: 1),
            Item(ContentItemStatus.Rendering),
        ]);

        report.TotalItems.ShouldBe(2);
        report.TerminalItems.ShouldBe(1);
        report.FirstAttemptPassRate.ShouldBe(1.0);
    }

    [Fact]
    public void With_nothing_terminal_yet_the_rate_is_null_rather_than_zero()
    {
        var report = QaPassRateCalculator.Calculate([Item(ContentItemStatus.Directing)]);

        report.FirstAttemptPassRate.ShouldBeNull();
    }

    [Fact]
    public void Needs_review_and_failed_items_count_toward_the_denominator_but_never_as_a_pass()
    {
        var report = QaPassRateCalculator.Calculate([
            Item(ContentItemStatus.Approved, attempts: 1),
            Item(ContentItemStatus.NeedsHumanReview, attempts: 3),
            Item(ContentItemStatus.Failed, attempts: 1),
        ]);

        report.TerminalItems.ShouldBe(3);
        report.NeedsReview.ShouldBe(1);
        report.Failed.ShouldBe(1);
        report.FirstAttemptPasses.ShouldBe(1);
        report.FirstAttemptPassRate.ShouldBe(1.0 / 3.0);
    }

    [Fact]
    public void A_realistic_mixed_week_computes_the_rate_the_plans_60_percent_floor_is_judged_against()
    {
        var report = QaPassRateCalculator.Calculate([
            Item(ContentItemStatus.Approved, attempts: 1),
            Item(ContentItemStatus.Approved, attempts: 1),
            Item(ContentItemStatus.Approved, attempts: 1),
            Item(ContentItemStatus.Approved, attempts: 2),
            Item(ContentItemStatus.NeedsHumanReview, attempts: 3),
        ]);

        report.FirstAttemptPassRate.ShouldBe(0.6);
    }
}
