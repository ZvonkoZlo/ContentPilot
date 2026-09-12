using ContentPilot.Domain.Common;

namespace ContentPilot.Domain.Evals;

/// <summary>
/// §27's eval strategy: one execution of the scenario catalogue, cassette or live. Not
/// tenant-owned — an eval run is a statement about the pipeline's own behaviour, not about
/// any one tenant's data, the same reason architecture tests or the renderer's golden tests
/// aren't tenant-scoped either.
/// <para>
/// Append-only, like every other record-of-what-happened in this codebase
/// (<c>AgentRun</c>, <c>CostEntry</c>, <c>QualityReview</c>): a trend page is only honest if
/// nobody can quietly edit last week's row after a prompt regressed.
/// </para>
/// </summary>
public sealed class EvalRun : Entity, IAppendOnly
{
    private EvalRun()
    {
    }

    public EvalRun(EvalMode mode, DateTimeOffset runAt, int totalScenarios, int passedScenarios)
    {
        Mode = mode;
        RunAt = runAt;
        TotalScenarios = Guard.InRange(totalScenarios, 0, 10_000);
        PassedScenarios = Guard.InRange(passedScenarios, 0, totalScenarios);
    }

    public EvalMode Mode { get; private set; }

    public DateTimeOffset RunAt { get; private set; }

    public int TotalScenarios { get; private set; }

    public int PassedScenarios { get; private set; }

    public bool AllPassed => TotalScenarios > 0 && PassedScenarios == TotalScenarios;
}

public enum EvalMode
{
    /// <summary>Recorded provider responses or hand-authored scripted ones. Every PR, free.</summary>
    Cassette = 0,

    /// <summary>Real providers, N runs for variance. Nightly, or before a prompt version bump.</summary>
    Live = 1,
}
