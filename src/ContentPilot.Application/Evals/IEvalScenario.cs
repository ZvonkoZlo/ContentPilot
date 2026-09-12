using ContentPilot.Domain.Evals;

namespace ContentPilot.Application.Evals;

/// <summary>
/// One row of §27's scenario catalogue, made runnable. A scenario is a pure question with a
/// pass/fail answer — "does the strategist avoid recent topics", not "is this a good week" —
/// so it can run in cassette mode, in CI, on every PR, for free.
/// </summary>
public interface IEvalScenario
{
    /// <summary>Matches the plan's own catalogue name, so a failing scenario is findable by grep.</summary>
    string Name { get; }

    EvalScenarioKind Kind { get; }

    Task<EvalScenarioOutcome> RunAsync(CancellationToken ct);
}

public sealed record EvalScenarioOutcome(bool Passed, string Detail)
{
    public static EvalScenarioOutcome Pass(string detail = "") => new(true, detail);

    public static EvalScenarioOutcome Fail(string detail) => new(false, detail);
}
