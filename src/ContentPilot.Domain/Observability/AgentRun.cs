using ContentPilot.Domain.Common;

namespace ContentPilot.Domain.Observability;

/// <summary>
/// One execution of one agent. Append-only, and the reason the whole system is explainable:
/// it names the exact prompt version, model and cost behind every piece of copy.
/// <para>
/// Payloads live in object storage, not here. Only references and metrics are in Postgres,
/// so the table stays queryable — "what did this campaign cost", "which prompt wrote this
/// caption" — without carrying megabytes of prompt text.
/// </para>
/// </summary>
public sealed class AgentRun : Entity, ITenantOwned, IAppendOnly
{
    private AgentRun()
    {
        AgentName = null!;
        AgentVersion = null!;
        ModelId = null!;
    }

    public AgentRun(
        Guid tenantId,
        Guid campaignId,
        Guid? contentItemId,
        string agentName,
        string agentVersion,
        Guid promptVersionId,
        string modelId,
        int attempt,
        DateTimeOffset startedAt)
    {
        TenantId = Guard.NotEmpty(tenantId);
        CampaignId = Guard.NotEmpty(campaignId);
        ContentItemId = contentItemId;
        AgentName = Guard.MaxLength(Guard.NotBlank(agentName), 80);
        AgentVersion = Guard.MaxLength(Guard.NotBlank(agentVersion), 20);
        PromptVersionId = Guard.NotEmpty(promptVersionId);
        ModelId = Guard.MaxLength(Guard.NotBlank(modelId), 120);
        Attempt = attempt;
        StartedAt = startedAt;
    }

    public Guid TenantId { get; private set; }

    public Guid CampaignId { get; private set; }

    /// <summary>Null for campaign-level agents such as the strategist.</summary>
    public Guid? ContentItemId { get; private set; }

    public string AgentName { get; private set; }

    /// <summary>Bumped when the agent's input or output contract changes.</summary>
    public string AgentVersion { get; private set; }

    public Guid PromptVersionId { get; private set; }

    public string ModelId { get; private set; }

    public int Attempt { get; private set; }

    public AgentRunOutcome Outcome { get; private set; } = AgentRunOutcome.Running;

    public long InputTokens { get; private set; }

    public long OutputTokens { get; private set; }

    public long CacheReadTokens { get; private set; }

    public long CacheWriteTokens { get; private set; }

    /// <summary>Micro-cents. LLM calls cost fractions of a cent; integers avoid drift.</summary>
    public long CostMicroCents { get; private set; }

    public int DurationMs { get; private set; }

    /// <summary>Storage keys for the full prompt and raw response, kept out of the database.</summary>
    public string? InputRef { get; private set; }

    public string? OutputRef { get; private set; }

    /// <summary>Which deterministic validator rejected the output, when one did.</summary>
    public string? ValidationFailure { get; private set; }

    public string? Error { get; private set; }

    /// <summary>Correlates this row with the OpenTelemetry trace covering the same work.</summary>
    public string? TraceId { get; private set; }

    public string? SpanId { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public void Succeed(TokenUsageSnapshot usage, long costMicroCents, int durationMs, DateTimeOffset completedAt)
    {
        Record(usage, costMicroCents, durationMs, completedAt);
        Outcome = AgentRunOutcome.Succeeded;
    }

    /// <summary>
    /// The model answered but the output failed a deterministic post-condition. Recorded
    /// separately from an error because it is a quality signal, not an outage.
    /// </summary>
    public void FailValidation(TokenUsageSnapshot usage, long costMicroCents, int durationMs, string failure, DateTimeOffset completedAt)
    {
        Record(usage, costMicroCents, durationMs, completedAt);
        ValidationFailure = Guard.MaxLength(Guard.NotBlank(failure), 2000);
        Outcome = AgentRunOutcome.ValidationFailed;
    }

    public void Fail(string error, int durationMs, DateTimeOffset completedAt)
    {
        Error = Guard.MaxLength(Guard.NotBlank(error), 2000);
        DurationMs = durationMs;
        CompletedAt = completedAt;
        Outcome = AgentRunOutcome.Failed;
    }

    public void AttachPayloads(string? inputRef, string? outputRef)
    {
        InputRef = inputRef;
        OutputRef = outputRef;
    }

    public void AttachTrace(string? traceId, string? spanId)
    {
        TraceId = traceId;
        SpanId = spanId;
    }

    private void Record(TokenUsageSnapshot usage, long costMicroCents, int durationMs, DateTimeOffset completedAt)
    {
        InputTokens = usage.InputTokens;
        OutputTokens = usage.OutputTokens;
        CacheReadTokens = usage.CacheReadTokens;
        CacheWriteTokens = usage.CacheWriteTokens;
        CostMicroCents = costMicroCents;
        DurationMs = durationMs;
        CompletedAt = completedAt;
    }
}

public readonly record struct TokenUsageSnapshot(long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens)
{
    public static readonly TokenUsageSnapshot Empty = new(0, 0, 0, 0);

    /// <summary>What the model actually read, cached or not.</summary>
    public long TotalInput => InputTokens + CacheReadTokens + CacheWriteTokens;
}

public enum AgentRunOutcome
{
    Running = 0,
    Succeeded = 1,

    /// <summary>Schema or validator rejection. Cheap to retry, and worth counting separately.</summary>
    ValidationFailed = 2,
    Failed = 3,
}
