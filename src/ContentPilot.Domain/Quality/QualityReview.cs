using ContentPilot.Domain.Common;

namespace ContentPilot.Domain.Quality;

/// <summary>
/// One gate's verdict on one attempt. Append-only: this is the record of why attempt 3
/// exists, and a QA pass rate that can be trusted depends on nobody being able to tidy up
/// an inconvenient row afterwards.
/// <para>
/// The deterministic gate writes one of these too, even when it passes. A gate that only
/// leaves a trace when it complains cannot answer "what fraction of first attempts pass?",
/// which is the metric that catches a miscalibrated QA prompt before it burns a budget.
/// </para>
/// </summary>
public sealed class QualityReview : Entity, ITenantOwned, IAppendOnly
{
    private QualityReview()
    {
        Findings = [];
    }

    public QualityReview(
        Guid tenantId,
        Guid contentItemId,
        int attempt,
        QaGate gate,
        QaOutcome outcome,
        double score,
        IReadOnlyList<QaFinding> findings,
        DateTimeOffset evaluatedAt,
        Guid? agentRunId = null,
        long durationMs = 0)
    {
        TenantId = Guard.NotEmpty(tenantId);
        ContentItemId = Guard.NotEmpty(contentItemId);
        Attempt = Guard.InRange(attempt, 1, 100);
        Gate = gate;
        Outcome = outcome;
        Score = score is >= 0 and <= 1
            ? score
            : throw new ArgumentOutOfRangeException(nameof(score), score, "Score must be between 0 and 1.");
        Findings = Validate(gate, findings);
        EvaluatedAt = evaluatedAt;
        AgentRunId = agentRunId;
        DurationMs = durationMs;
    }

    public Guid TenantId { get; private set; }

    public Guid ContentItemId { get; private set; }

    /// <summary>The quality attempt this gate ran against. One row per gate per attempt.</summary>
    public int Attempt { get; private set; }

    public QaGate Gate { get; private set; }

    public QaOutcome Outcome { get; private set; }

    /// <summary>
    /// 0 to 1, worst to clean. Used to promote the best attempt when an item runs out of
    /// attempts, so work is never thrown away.
    /// </summary>
    public double Score { get; private set; }

    public IReadOnlyList<QaFinding> Findings { get; private set; }

    public DateTimeOffset EvaluatedAt { get; private set; }

    /// <summary>Set for the model gates, so a finding can be traced to the run that made it.</summary>
    public Guid? AgentRunId { get; private set; }

    public long DurationMs { get; private set; }

    public bool HasBlockingFindings => Findings.Any(f => f.Severity == QaSeverity.Blocking);

    /// <summary>
    /// A model gate must not report a finding the deterministic gate owns. Letting a vision
    /// model claim text overflow would put judgement in front of measurement on a question
    /// measurement answers exactly.
    /// </summary>
    private static IReadOnlyList<QaFinding> Validate(QaGate gate, IReadOnlyList<QaFinding> findings)
    {
        var foreign = findings.FirstOrDefault(f => !QaFindingCodes.BelongsTo(f.Code, gate));

        if (foreign is not null)
        {
            throw new ArgumentException(
                $"{foreign.Code} belongs to the {QaFindingCodes.GateFor(foreign.Code)} gate and cannot be reported by {gate}.",
                nameof(findings));
        }

        return [.. findings];
    }
}
