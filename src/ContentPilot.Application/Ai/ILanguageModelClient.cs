namespace ContentPilot.Application.Ai;

/// <summary>
/// The only way anything in this system talks to a language model.
/// <para>
/// Every call is structured: a schema goes in, a validated object comes out. There is no
/// "give me some text" method, because free-form output is what makes an LLM pipeline
/// impossible to test — a caller that cannot state the shape it expects cannot check that
/// it got it.
/// </para>
/// <para>
/// The interface names a <em>profile</em>, never a model. Which model a profile resolves to
/// is configuration, so changing it never touches an agent.
/// </para>
/// </summary>
public interface ILanguageModelClient
{
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default);
}

public sealed record LlmRequest
{
    /// <summary>A profile name such as <c>strategist</c>, resolved from configuration.</summary>
    public required string Profile { get; init; }

    /// <summary>
    /// Stable across calls, so it sits at the front of the cached prefix. Anything that
    /// varies per request belongs in <see cref="User"/> instead.
    /// </summary>
    public required string System { get; init; }

    public required string User { get; init; }

    /// <summary>
    /// The JSON Schema the response must satisfy, enforced by the provider rather than
    /// hoped for. Without this the first thing every agent would need is a parser that
    /// tolerates the model's mood.
    /// </summary>
    public required string ResponseSchema { get; init; }

    /// <summary>Identifies the call in traces, cost rows and cassette files.</summary>
    public required string Operation { get; init; }

    /// <summary>Correlates spend with a campaign. Null only for diagnostics.</summary>
    public Guid? CampaignId { get; init; }

    public Guid? ContentItemId { get; init; }

    /// <summary>
    /// Deliberate: cache the system prompt but not the user turn. The system half carries
    /// the brand block and the instructions and repeats across an entire campaign; the user
    /// half is different every time and would only invalidate the prefix.
    /// </summary>
    public bool CacheSystemPrompt { get; init; } = true;

    public int? MaxOutputTokens { get; init; }

    /// <summary>
    /// Attached to the user turn, in order, before the text. Empty for every text-only
    /// agent — only a vision-capable one (VisualQA) ever populates this.
    /// </summary>
    public IReadOnlyList<LlmImageAttachment> Images { get; init; } = [];
}

/// <summary>
/// One image attached to a call. Deliberately not <c>Rendering.Contracts.ImagePayload</c> —
/// this is a wire shape for a model turn, not a render artifact, and the two should be free
/// to diverge even though today they carry the same two fields.
/// </summary>
public sealed record LlmImageAttachment
{
    public required string MediaType { get; init; }

    public required string Base64Data { get; init; }
}

public sealed record LlmResponse
{
    /// <summary>Raw JSON, already validated against the requested schema.</summary>
    public required string Json { get; init; }

    public required string ModelId { get; init; }

    public required TokenUsage Usage { get; init; }

    public required int DurationMs { get; init; }

    /// <summary>Micro-cents, priced from the profile's snapshotted rates.</summary>
    public required long CostMicroCents { get; init; }

    /// <summary>True when a cassette answered instead of the provider.</summary>
    public bool FromCassette { get; init; }

    /// <summary>
    /// The model declined. A real outcome with a category, not an exception — the caller
    /// decides whether to fall back, rephrase, or escalate to a human.
    /// </summary>
    public string? RefusalCategory { get; init; }
}

public readonly record struct TokenUsage(long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens)
{
    public static readonly TokenUsage Empty = default;
}

/// <summary>
/// The model produced valid JSON that the schema rejected, twice. Distinct from a transport
/// failure because retrying it costs money and rarely helps — the prompt is wrong.
/// </summary>
public sealed class LlmSchemaException(string message) : Exception(message);

/// <summary>The campaign has spent its budget. Never retried; the orchestrator decides.</summary>
public sealed class LlmBudgetExceededException(string message) : Exception(message);
