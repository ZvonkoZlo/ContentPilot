namespace ContentPilot.Application.Agents;

/// <summary>
/// An agent turns an input into a validated output, and does nothing else.
/// <para>
/// It builds its prompt variables and checks its own output; it does not call the model,
/// write a row, archive a payload or decide whether to retry. Those belong to the executor,
/// which is the only thing that touches the outside world. An architecture test enforces
/// the boundary, so an agent cannot decide what happens next even if a future prompt tells
/// it to.
/// </para>
/// <para>
/// The split has a second payoff: every agent is testable as a pure function. Give it an
/// input and a recorded response and you can assert on what it accepts and rejects without
/// a key, a network or a database.
/// </para>
/// </summary>
public interface IAgent<in TInput, TOutput>
{
    /// <summary>Recorded on every run, so a bad output can be traced to a specific agent.</summary>
    string Name { get; }

    /// <summary>Bumped when the input or output contract changes, not when the prompt does.</summary>
    string Version { get; }

    /// <summary>Which prompt to render. The prompt names the model profile, not the agent.</summary>
    string PromptId { get; }

    /// <summary>The schema the provider enforces on the response.</summary>
    string ResponseSchema { get; }

    /// <summary>Values for the prompt's placeholders. Strict: missing or extra names throw.</summary>
    IReadOnlyDictionary<string, string> BuildVariables(TInput input);

    /// <summary>
    /// Images attached to the call, in order. Empty for every text-only agent — a default
    /// interface member so Directing, Writing and every other non-visual agent needs no
    /// change; only a vision-capable agent (VisualQA) overrides it.
    /// </summary>
    IReadOnlyList<Ai.LlmImageAttachment> BuildImages(TInput input) => [];

    /// <summary>
    /// Deterministic post-conditions. An empty list means the output is usable; anything
    /// else is fed back to the model as a repair instruction, and counted against the
    /// item's quality budget rather than treated as an outage.
    /// </summary>
    IReadOnlyList<string> Validate(TOutput output, TInput input);
}

/// <summary>What an executor returns once an agent has produced something usable.</summary>
public sealed record AgentResult<T>
{
    public required T Value { get; init; }

    /// <summary>Identifies the exact prompt text behind this output.</summary>
    public required string PromptReference { get; init; }

    public required string ModelId { get; init; }

    /// <summary>
    /// How many times the output had to be repaired before it validated. Worth watching:
    /// a prompt that regularly needs two attempts is a prompt that needs rewriting, and
    /// this is the number that says so.
    /// </summary>
    public required int Attempts { get; init; }

    public required long CostMicroCents { get; init; }

    /// <summary>Problems from earlier attempts, kept so a rising trend is visible.</summary>
    public IReadOnlyList<string> RepairedProblems { get; init; } = [];
}

/// <summary>
/// The agent could not produce output that satisfies its own post-conditions within the
/// allowed attempts. The orchestrator decides what happens next — never the agent.
/// </summary>
public sealed class AgentValidationException(string agentName, IReadOnlyList<string> problems)
    : Exception($"{agentName} could not produce a valid result. Last problems: {string.Join("; ", problems)}")
{
    public IReadOnlyList<string> Problems { get; } = problems;
}
