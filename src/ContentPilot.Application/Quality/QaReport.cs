using ContentPilot.Domain.Quality;

namespace ContentPilot.Application.Quality;

/// <summary>
/// One gate's result before it becomes a row. The outcome is derived from the findings
/// rather than decided alongside them, so a gate cannot report a blocking defect and still
/// call itself a pass.
/// </summary>
public sealed record QaReport
{
    public required QaGate Gate { get; init; }

    public required IReadOnlyList<QaFinding> Findings { get; init; }

    /// <summary>Milliseconds the gate took. Gate 1 should be single-digit.</summary>
    public long DurationMs { get; init; }

    public QaOutcome Outcome => Findings.Count == 0
        ? QaOutcome.Pass
        : Findings.Max(f => f.Severity) switch
        {
            QaSeverity.Blocking => QaOutcome.Fail,
            QaSeverity.Major => QaOutcome.NeedsReview,
            _ => QaOutcome.Pass,
        };

    /// <summary>
    /// 0 to 1, worst to clean. Its one job is ranking attempts against each other so the
    /// best one can be promoted when an item runs out of retries — it is deliberately not a
    /// pass/fail input, because a threshold on a weighted sum is exactly the kind of number
    /// nobody can explain six weeks later.
    /// </summary>
    public double Score
    {
        get
        {
            var penalty = Findings.Sum(f => f.Severity switch
            {
                QaSeverity.Blocking => 1.0,
                QaSeverity.Major => 0.34,
                QaSeverity.Minor => 0.08,
                _ => 0.0,
            });

            return Math.Clamp(1.0 - penalty, 0.0, 1.0);
        }
    }

    public QualityReview ToReview(
        Guid tenantId,
        Guid contentItemId,
        int attempt,
        DateTimeOffset evaluatedAt,
        Guid? agentRunId = null) =>
        new(tenantId, contentItemId, attempt, Gate, Outcome, Score, Findings, evaluatedAt, agentRunId, DurationMs);
}
