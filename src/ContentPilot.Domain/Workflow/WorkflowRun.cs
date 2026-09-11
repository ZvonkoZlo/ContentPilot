using ContentPilot.Domain.Common;
using ContentPilot.Domain.Tenancy;

namespace ContentPilot.Domain.Workflow;

/// <summary>
/// The orchestrator's own bookkeeping for one campaign or one item, kept apart from the
/// entity's business status (<c>ContentCampaign.Status</c>, <c>ContentItem.Status</c>) on
/// purpose. Business status answers "what is this, right now"; a run answers "how many
/// times have we tried, against what deadline, and is anyone allowed to touch it".
/// <para>
/// The core loop is a pure function over this plus its steps:
/// <c>(WorkflowRun, WorkflowStep[]) =&gt; NextAction</c>. A worker leases the run, computes
/// one action, executes it, and persists the step and the new counters in the same
/// transaction the business entity's transition commits in — which is what makes a crash
/// mid-step resumable rather than silently duplicated.
/// </para>
/// <para>
/// Every ceiling here is read from <see cref="TenantLimits"/> at construction, never
/// hard-coded on the run itself — the plan is explicit that tuning these is data, never a
/// deploy, and a run started under one set of limits keeps them even if the tenant's
/// configuration changes mid-flight.
/// </para>
/// </summary>
public sealed class WorkflowRun : Entity, ITenantOwned, IAuditable
{
    private WorkflowRun()
    {
    }

    public WorkflowRun(
        Guid tenantId,
        Guid campaignId,
        WorkflowScope scope,
        Guid entityId,
        DateTimeOffset startedAt,
        TenantLimits? limits = null)
    {
        var effective = limits ?? TenantLimits.Default;

        TenantId = Guard.NotEmpty(tenantId);
        CampaignId = Guard.NotEmpty(campaignId);
        Scope = scope;
        EntityId = Guard.NotEmpty(entityId);
        StartedAt = startedAt;
        Deadline = startedAt + effective.MaxRunDuration;
        MaxSchemaRepairAttempts = effective.MaxSchemaRepairAttempts;
        MaxTransientAttempts = effective.MaxTransientAttempts;
        MaxQualityAttempts = effective.MaxQualityAttemptsPerItem;
        MaxStepsExecuted = effective.MaxStepsPerItem;
        State = WorkflowRunState.Active;
    }

    public Guid TenantId { get; private set; }

    public Guid CampaignId { get; private set; }

    public WorkflowScope Scope { get; private set; }

    /// <summary>The campaign id when <see cref="Scope"/> is Campaign, the item id when Item.</summary>
    public Guid EntityId { get; private set; }

    public WorkflowRunState State { get; private set; }

    public int SchemaRepairAttempts { get; private set; }

    public int TransientAttempts { get; private set; }

    /// <summary>The escalation ladder counter. This is the one the remediation router reads.</summary>
    public int QualityAttempts { get; private set; }

    public int MaxSchemaRepairAttempts { get; private set; }

    public int MaxTransientAttempts { get; private set; }

    public int MaxQualityAttempts { get; private set; }

    public int StepsExecuted { get; private set; }

    /// <summary>A hard ceiling regardless of what any other counter says.</summary>
    public int MaxStepsExecuted { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    /// <summary>Past this, the run is force-terminated regardless of what its counters say.</summary>
    public DateTimeOffset Deadline { get; private set; }

    public DateTimeOffset? LeaseUntil { get; private set; }

    public string? LeaseOwner { get; private set; }

    public string? LastError { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }

    public bool IsTerminal => State is not WorkflowRunState.Active;

    public bool SchemaRepairExhausted => SchemaRepairAttempts >= MaxSchemaRepairAttempts;

    public bool TransientExhausted => TransientAttempts >= MaxTransientAttempts;

    public bool QualityExhausted => QualityAttempts >= MaxQualityAttempts;

    public bool StepBudgetExhausted => StepsExecuted >= MaxStepsExecuted;

    public bool IsPastDeadline(DateTimeOffset now) => now >= Deadline;

    /// <summary>
    /// Defense in depth against two jobs racing the same run: a lease held by someone else
    /// and not yet expired refuses a second lease outright, on top of whatever the job queue
    /// itself already guarantees about not double-dispatching one job.
    /// </summary>
    public void Lease(string owner, DateTimeOffset now, TimeSpan duration)
    {
        if (IsTerminal)
        {
            throw new InvalidOperationException($"Run {Id} is {State} and cannot be leased.");
        }

        if (LeaseUntil is { } until && until > now && !string.Equals(LeaseOwner, owner, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Run {Id} is already leased by {LeaseOwner} until {until:O}.");
        }

        LeaseOwner = Guard.NotBlank(owner);
        LeaseUntil = now + duration;
    }

    public void ReleaseLease()
    {
        LeaseOwner = null;
        LeaseUntil = null;
    }

    public void RecordStep() => StepsExecuted++;

    /// <summary>Cheap and does not touch the quality ladder — a malformed response is not a bad idea.</summary>
    public void RecordSchemaRepair() => SchemaRepairAttempts++;

    /// <summary>A provider outage is not a quality failure either.</summary>
    public void RecordTransientRetry() => TransientAttempts++;

    /// <summary>The one counter the escalation ladder in §8 is keyed on.</summary>
    public void RecordQualityAttempt() => QualityAttempts++;

    public void Complete(DateTimeOffset now)
    {
        State = WorkflowRunState.Completed;
        CompletedAt = now;
        ReleaseLease();
    }

    public void SendToHumanReview(string reason, DateTimeOffset now)
    {
        LastError = Guard.MaxLength(Guard.NotBlank(reason), 1000);
        State = WorkflowRunState.NeedsHumanReview;
        CompletedAt = now;
        ReleaseLease();
    }

    public void Fail(string reason, DateTimeOffset now)
    {
        LastError = Guard.MaxLength(Guard.NotBlank(reason), 1000);
        State = WorkflowRunState.Failed;
        CompletedAt = now;
        ReleaseLease();
    }
}

public enum WorkflowScope
{
    Campaign = 0,
    Item = 1,
}

public enum WorkflowRunState
{
    Active = 0,
    Completed = 1,

    /// <summary>Not a failure. The best attempt is retained; a human decides what happens next.</summary>
    NeedsHumanReview = 2,
    Failed = 3,
}
