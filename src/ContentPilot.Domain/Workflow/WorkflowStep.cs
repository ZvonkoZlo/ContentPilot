using ContentPilot.Domain.Common;

namespace ContentPilot.Domain.Workflow;

/// <summary>
/// One executed step of a <see cref="WorkflowRun"/>. Append-only: this is the audit trail
/// that lets a stuck or finished run be read back from SQL without guessing, and it is what
/// idempotency is checked against — a replayed job for the same
/// <c>(EntityId, StepName, Attempt)</c> finds its row already here and becomes a no-op.
/// <para>
/// <see cref="StepName"/> is a string rather than a shared enum because a run can be at
/// campaign scope or item scope, and the two levels use different vocabularies
/// (<c>CampaignStatus</c> and <c>ContentItemStatus</c>) that this table has no reason to
/// depend on.
/// </para>
/// </summary>
public sealed class WorkflowStep : Entity, ITenantOwned, IAppendOnly
{
    private WorkflowStep()
    {
        StepName = null!;
        IdempotencyKey = null!;
    }

    public WorkflowStep(
        Guid tenantId,
        Guid workflowRunId,
        string stepName,
        int attempt,
        DateTimeOffset startedAt)
    {
        TenantId = Guard.NotEmpty(tenantId);
        WorkflowRunId = Guard.NotEmpty(workflowRunId);
        StepName = Guard.MaxLength(Guard.NotBlank(stepName), 80);
        Attempt = Guard.InRange(attempt, 1, 1000);
        IdempotencyKey = IdempotencyKeyFor(workflowRunId, stepName, attempt);
        StartedAt = startedAt;
        Outcome = WorkflowStepOutcome.Running;
    }

    public Guid TenantId { get; private set; }

    public Guid WorkflowRunId { get; private set; }

    public string StepName { get; private set; }

    public int Attempt { get; private set; }

    /// <summary>
    /// <c>(WorkflowRunId, StepName, Attempt)</c> composed into one string, unique-indexed —
    /// this is the whole idempotency mechanism from §6.
    /// </summary>
    public string IdempotencyKey { get; private set; }

    public WorkflowStepOutcome Outcome { get; private set; }

    /// <summary>Set for steps that called an agent, so cost and prompt version are traceable.</summary>
    public Guid? AgentRunId { get; private set; }

    public string? Error { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public void Succeed(DateTimeOffset now, Guid? agentRunId = null)
    {
        Outcome = WorkflowStepOutcome.Succeeded;
        AgentRunId = agentRunId;
        CompletedAt = now;
    }

    public void Fail(string error, DateTimeOffset now)
    {
        Outcome = WorkflowStepOutcome.Failed;
        Error = Guard.MaxLength(Guard.NotBlank(error), 2000);
        CompletedAt = now;
    }

    public static string IdempotencyKeyFor(Guid workflowRunId, string stepName, int attempt) =>
        $"{workflowRunId:N}:{stepName}:{attempt}";
}

public enum WorkflowStepOutcome
{
    Running = 0,
    Succeeded = 1,
    Failed = 2,
}
