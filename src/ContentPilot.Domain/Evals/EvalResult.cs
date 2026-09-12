using ContentPilot.Domain.Common;

namespace ContentPilot.Domain.Evals;

/// <summary>
/// One scenario's outcome within an <see cref="EvalRun"/>. Append-only for the same reason
/// the run itself is.
/// </summary>
public sealed class EvalResult : Entity, IAppendOnly
{
    private EvalResult()
    {
        ScenarioName = null!;
        Detail = null!;
    }

    public EvalResult(Guid evalRunId, string scenarioName, EvalScenarioKind kind, bool passed, string detail, int durationMs)
    {
        EvalRunId = Guard.NotEmpty(evalRunId);
        ScenarioName = Guard.MaxLength(Guard.NotBlank(scenarioName), 200);
        Kind = kind;
        Passed = passed;
        Detail = Guard.MaxLength(detail, 2000);
        DurationMs = durationMs;
    }

    public Guid EvalRunId { get; private set; }

    public string ScenarioName { get; private set; }

    public EvalScenarioKind Kind { get; private set; }

    public bool Passed { get; private set; }

    /// <summary>Why it passed or failed — the assertion's own message, not a stack trace.</summary>
    public string Detail { get; private set; }

    public int DurationMs { get; private set; }
}

/// <summary>§27's own catalogue column: how much this scenario's assertion can be trusted.</summary>
public enum EvalScenarioKind
{
    /// <summary>Pure computation — counting, matching, hashing. No model involved.</summary>
    Deterministic = 0,

    /// <summary>A model call judged against hand-labelled expected findings, not a rubric.</summary>
    DeterministicLabels = 1,

    /// <summary>Deterministic checks plus one LLM-judge assertion in the same scenario.</summary>
    Mixed = 2,

    /// <summary>Rubric-scored by a judge, or the operator's own rating.</summary>
    JudgeAndHuman = 3,
}
