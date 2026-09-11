using ContentPilot.Application.Quality;
using ContentPilot.Domain.Content;
using ContentPilot.Domain.Quality;
using ContentPilot.Domain.Workflow;

namespace ContentPilot.Application.Orchestration;

/// <summary>
/// The core loop from §6, as the pure function the plan names directly:
/// <c>(WorkflowRun, WorkflowStep[]) =&gt; NextAction</c>. A worker leases the run, calls
/// <see cref="Decide"/> exactly once, executes the single action it names, and persists the
/// result and the new counters in one transaction before re-enqueueing — that sequence is
/// what makes a crash mid-step resumable rather than silently duplicated, and it is the
/// caller's job, not this type's: nothing here touches a database, a queue, or a model.
/// <para>
/// This is where the pieces already built stop being separate policies and become one
/// decision: <see cref="ItemStateMachine"/> supplies the legal shape, <see cref="QaReport"/>
/// supplies the evidence, <see cref="RemediationRouter"/> supplies the fix, and
/// <see cref="BudgetGuard"/>'s verdict (computed by the caller from the ledger, since that
/// part genuinely needs the database) supplies the one guard this function cannot compute
/// for itself.
/// </para>
/// </summary>
public static class OrchestratorCore
{
    public static NextAction Decide(WorkflowDecisionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var run = context.Run;

        if (run.IsTerminal)
        {
            // Calling Decide on a run that already reached a terminal state is a bug in the
            // caller — it means a job was enqueued for work that is already finished — so
            // this throws rather than quietly returning another terminal action.
            throw new InvalidOperationException($"WorkflowRun {run.Id} is already {run.State} and cannot be decided on.");
        }

        // Belt-and-braces guards, checked before anything about the current step matters.
        // Each one ends the run at NeedsHumanReview rather than a bare failure, because the
        // best attempt so far is still worth keeping — §7's design note applies here too.
        if (run.IsPastDeadline(context.Now))
        {
            return NextAction.ToHumanReview(
                $"Wall-clock deadline of {run.Deadline:O} was exceeded.");
        }

        if (run.StepBudgetExhausted)
        {
            return NextAction.ToHumanReview(
                $"Step cap of {run.MaxStepsExecuted} steps was reached.");
        }

        if (context.Budget is { Allowed: false } budget)
        {
            return NextAction.ToHumanReview(
                $"{budget.Scope} budget would be exceeded: projected {budget.ProjectedMicroCents} " +
                $"micro-cents against a limit of {budget.LimitMicroCents}.");
        }

        if (context.CurrentStep == ContentItemStatus.Approved)
        {
            return NextAction.Complete();
        }

        // The ordinary case: keep executing the current step. Everything interesting
        // happens once Validating has actually run and produced a verdict — until then
        // there is nothing to decide, only the next step to perform.
        if (context.CurrentStep != ContentItemStatus.Validating || context.QaReport is null)
        {
            return NextAction.Execute(context.CurrentStep);
        }

        return DecideAfterValidation(run, context);
    }

    private static NextAction DecideAfterValidation(WorkflowRun run, WorkflowDecisionContext context)
    {
        var report = context.QaReport!;

        if (report.Outcome == QaOutcome.Pass)
        {
            return NextAction.Complete();
        }

        var finding = RemediationRouter.PrimaryFinding(report.Findings);

        if (finding is null)
        {
            // QaReport.Outcome is derived from the worst finding's severity, so a non-pass
            // outcome should always carry a Major or Blocking finding to act on. If it does
            // not, that is a gap between a gate and the router worth a human's attention
            // rather than an attempt spent guessing — never a silent approval of content a
            // gate just refused to pass.
            return NextAction.ToHumanReview(
                $"{report.Gate} reported {report.Outcome} with no finding at Major or above to route on.");
        }

        var decision = RemediationRouter.Decide(
            finding,
            ContentItemStatus.Validating,
            run.QualityAttempts,
            run.MaxQualityAttempts,
            context.PreviousRemediation);

        return decision.Outcome switch
        {
            RemediationOutcome.Restart or RemediationOutcome.SafeModeFallback => NextAction.Remediate(decision),
            RemediationOutcome.Replan => NextAction.Replan(decision.Reason),
            RemediationOutcome.NeedsHumanReview => NextAction.ToHumanReview(decision.Reason),
            _ => throw new InvalidOperationException($"Unhandled remediation outcome {decision.Outcome}."),
        };
    }
}

/// <summary>
/// Everything <see cref="OrchestratorCore.Decide"/> needs, assembled by the caller from the
/// run row, the item's current status, and — only on a Validating step — the QA report that
/// step just produced. <see cref="Budget"/> is likewise computed by the caller via
/// <see cref="BudgetGuard"/>, since summing the ledger is a database concern this function
/// deliberately has no access to.
/// </summary>
public sealed record WorkflowDecisionContext
{
    public required WorkflowRun Run { get; init; }

    /// <summary>The item's current step. The step about to execute, not one already finished.</summary>
    public required ContentItemStatus CurrentStep { get; init; }

    public required DateTimeOffset Now { get; init; }

    /// <summary>Present only when <see cref="CurrentStep"/> is Validating and QA has just run.</summary>
    public QaReport? QaReport { get; init; }

    /// <summary>
    /// The (code, target) pair the previous remediation attempt acted on, for the repeated-
    /// fix escalation rule in §8. Null on the item's first remediation.
    /// </summary>
    public (QaFindingCode Code, ContentItemStatus Target)? PreviousRemediation { get; init; }

    /// <summary>Null when no billable call is about to be made — most steps do not need one.</summary>
    public BudgetDecision? Budget { get; init; }
}

public enum NextActionKind
{
    /// <summary>Run the named step.</summary>
    ExecuteStep,

    /// <summary>Validation is clean; the item is approved.</summary>
    Complete,

    /// <summary>Restart at an earlier step, per the remediation router.</summary>
    Remediate,

    /// <summary>Escalate to the campaign: the strategist must re-plan this one item.</summary>
    Replan,

    /// <summary>Attempts, budget, steps or the deadline ran out. The best attempt is kept.</summary>
    NeedsHumanReview,
}

public sealed record NextAction
{
    public required NextActionKind Kind { get; init; }

    /// <summary>The step to run, set for ExecuteStep and Remediate.</summary>
    public ContentItemStatus? Step { get; init; }

    /// <summary>Set only for Remediate, so the caller can log the rung and write a ContentRevision.</summary>
    public RemediationDecision? Remediation { get; init; }

    public string? Reason { get; init; }

    public static NextAction Execute(ContentItemStatus step) => new()
    {
        Kind = NextActionKind.ExecuteStep,
        Step = step,
    };

    public static NextAction Complete() => new() { Kind = NextActionKind.Complete };

    public static NextAction Remediate(RemediationDecision decision) => new()
    {
        Kind = NextActionKind.Remediate,
        Step = decision.RestartAt,
        Remediation = decision,
        Reason = decision.Reason,
    };

    public static NextAction Replan(string reason) => new()
    {
        Kind = NextActionKind.Replan,
        Reason = reason,
    };

    public static NextAction ToHumanReview(string reason) => new()
    {
        Kind = NextActionKind.NeedsHumanReview,
        Reason = reason,
    };
}
