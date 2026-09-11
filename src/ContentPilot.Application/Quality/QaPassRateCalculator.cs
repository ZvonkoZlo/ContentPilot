using ContentPilot.Domain.Content;

namespace ContentPilot.Application.Quality;

/// <summary>
/// §9's headline metric: what fraction of items pass QA on the first attempt, without any
/// remediation restart. A pass rate below the plan's own 60% floor means the deterministic
/// checks or the model gates are miscalibrated — too strict, or the templates/prompts feeding
/// them are producing bad output — and it is the number that answers that question, not any
/// single item's outcome.
/// <para>
/// Pure and synchronous by design, like every other calculator in this codebase
/// (<c>DeterministicQaSuite</c>, <c>RemediationRouter</c>): it takes a snapshot of item state,
/// not a database connection, so it is trivially unit-testable and reusable from a campaign
/// endpoint today and a tenant-wide dashboard or a nightly eval report later without change.
/// </para>
/// </summary>
public static class QaPassRateCalculator
{
    public static QaPassRateReport Calculate(IReadOnlyList<QaPassRateItem> items)
    {
        var terminal = items.Where(i => i.Status is ContentItemStatus.Approved
            or ContentItemStatus.NeedsHumanReview or ContentItemStatus.Failed).ToList();

        var approved = terminal.Count(i => i.Status == ContentItemStatus.Approved);
        var firstAttemptPasses = terminal.Count(i => i.Status == ContentItemStatus.Approved && i.QualityAttempts <= 1);
        var needsReview = terminal.Count(i => i.Status == ContentItemStatus.NeedsHumanReview);
        var failed = terminal.Count(i => i.Status == ContentItemStatus.Failed);

        return new QaPassRateReport
        {
            TotalItems = items.Count,
            TerminalItems = terminal.Count,
            Approved = approved,
            FirstAttemptPasses = firstAttemptPasses,
            NeedsReview = needsReview,
            Failed = failed,
            // Denominator is terminal items, not every item: one still in flight is neither
            // a pass nor a failure yet, and counting it as a miss would understate the rate
            // for a campaign that simply hasn't finished.
            FirstAttemptPassRate = terminal.Count == 0 ? null : (double)firstAttemptPasses / terminal.Count,
        };
    }
}

public sealed record QaPassRateItem
{
    public required ContentItemStatus Status { get; init; }

    public required int QualityAttempts { get; init; }
}

public sealed record QaPassRateReport
{
    public required int TotalItems { get; init; }

    public required int TerminalItems { get; init; }

    public required int Approved { get; init; }

    public required int FirstAttemptPasses { get; init; }

    public required int NeedsReview { get; init; }

    public required int Failed { get; init; }

    /// <summary>Null when no item has reached a terminal state yet — nothing to measure.</summary>
    public required double? FirstAttemptPassRate { get; init; }
}
