using ContentPilot.Application.Orchestration;
using ContentPilot.Application.Quality;
using ContentPilot.Domain.Content;
using ContentPilot.Domain.Quality;
using ContentPilot.Domain.Tenancy;
using ContentPilot.Domain.Workflow;
using Shouldly;

namespace ContentPilot.UnitTests.Orchestration;

/// <summary>
/// §6's core loop, as the pure decision function the plan names directly. Every test here
/// builds a <see cref="WorkflowDecisionContext"/> by hand rather than through a live run, so
/// the whole surface — guards, the happy path, and every QA outcome — is exercised without a
/// database or a queue in sight.
/// </summary>
public sealed class OrchestratorCoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 8, 0, 0, TimeSpan.Zero);

    private static WorkflowRun Run(TenantLimits? limits = null) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), WorkflowScope.Item, Guid.CreateVersion7(), Now, limits);

    private static WorkflowDecisionContext Context(
        WorkflowRun run,
        ContentItemStatus step,
        DateTimeOffset? now = null,
        QaReport? qaReport = null,
        BudgetDecision? budget = null,
        (QaFindingCode, ContentItemStatus)? previous = null) => new()
    {
        Run = run,
        CurrentStep = step,
        Now = now ?? Now,
        QaReport = qaReport,
        Budget = budget,
        PreviousRemediation = previous,
    };

    private static QaReport Report(params QaFinding[] findings) => new()
    {
        Gate = QaGate.Deterministic,
        Findings = findings,
    };

    [Fact]
    public void Deciding_on_a_terminal_run_is_refused()
    {
        var run = Run();
        run.Complete(Now);

        Should.Throw<InvalidOperationException>(() =>
            OrchestratorCore.Decide(Context(run, ContentItemStatus.Writing)));
    }

    [Fact]
    public void Past_the_deadline_the_run_goes_to_human_review_regardless_of_step()
    {
        var run = Run(TenantLimits.Default with { MaxRunDuration = TimeSpan.FromMinutes(10) });

        var action = OrchestratorCore.Decide(
            Context(run, ContentItemStatus.Directing, now: Now + TimeSpan.FromMinutes(11)));

        action.Kind.ShouldBe(NextActionKind.NeedsHumanReview);
    }

    [Fact]
    public void A_step_cap_reached_ends_in_human_review()
    {
        var run = Run(TenantLimits.Default with { MaxStepsPerItem = 1 });
        run.RecordStep();

        var action = OrchestratorCore.Decide(Context(run, ContentItemStatus.Writing));

        action.Kind.ShouldBe(NextActionKind.NeedsHumanReview);
    }

    [Fact]
    public void A_refused_budget_check_ends_in_human_review_before_anything_else_is_considered()
    {
        var run = Run();
        var budget = BudgetDecision.Exceeded(BudgetScope.Item, 70_000, 60_000);

        var action = OrchestratorCore.Decide(Context(run, ContentItemStatus.AssetGeneration, budget: budget));

        action.Kind.ShouldBe(NextActionKind.NeedsHumanReview);
        action.Reason.ShouldNotBeNull();
        action.Reason.ShouldContain("Item");
    }

    [Fact]
    public void An_allowed_budget_check_does_not_block_the_step()
    {
        var run = Run();
        var budget = BudgetDecision.Allow(BudgetScope.Item, 30_000, 60_000);

        var action = OrchestratorCore.Decide(Context(run, ContentItemStatus.AssetGeneration, budget: budget));

        action.Kind.ShouldBe(NextActionKind.ExecuteStep);
    }

    [Theory]
    [InlineData(ContentItemStatus.Pending)]
    [InlineData(ContentItemStatus.Directing)]
    [InlineData(ContentItemStatus.Writing)]
    [InlineData(ContentItemStatus.SpecAssembly)]
    [InlineData(ContentItemStatus.AssetGeneration)]
    [InlineData(ContentItemStatus.Rendering)]
    public void Before_validation_the_decision_is_simply_to_run_the_current_step(ContentItemStatus step)
    {
        var action = OrchestratorCore.Decide(Context(Run(), step));

        action.Kind.ShouldBe(NextActionKind.ExecuteStep);
        action.Step.ShouldBe(step);
    }

    [Fact]
    public void Validating_with_no_report_yet_just_means_run_validation()
    {
        // The report is only present once the gate has actually returned a verdict for
        // this attempt — before that, Validating is executed like any other step.
        var action = OrchestratorCore.Decide(Context(Run(), ContentItemStatus.Validating, qaReport: null));

        action.Kind.ShouldBe(NextActionKind.ExecuteStep);
        action.Step.ShouldBe(ContentItemStatus.Validating);
    }

    [Fact]
    public void A_clean_validation_completes_the_item()
    {
        var action = OrchestratorCore.Decide(
            Context(Run(), ContentItemStatus.Validating, qaReport: Report()));

        action.Kind.ShouldBe(NextActionKind.Complete);
    }

    [Fact]
    public void Reaching_approved_completes_the_run_without_re_running_anything()
    {
        var action = OrchestratorCore.Decide(Context(Run(), ContentItemStatus.Approved));

        action.Kind.ShouldBe(NextActionKind.Complete);
    }

    [Fact]
    public void A_blocking_finding_routes_through_the_remediation_router()
    {
        var finding = new QaFinding { Code = QaFindingCode.TextOverflow, Severity = QaSeverity.Blocking };

        var action = OrchestratorCore.Decide(
            Context(Run(), ContentItemStatus.Validating, qaReport: Report(finding)));

        action.Kind.ShouldBe(NextActionKind.Remediate);
        action.Step.ShouldBe(ContentItemStatus.Writing);
        action.Remediation.ShouldNotBeNull();
        action.Remediation!.Rung.ShouldBe(1);
    }

    [Fact]
    public void The_previous_remediation_pair_carries_through_to_the_repeat_escalation_rule()
    {
        var finding = new QaFinding { Code = QaFindingCode.LowContrast, Severity = QaSeverity.Blocking };
        var previous = (QaFindingCode.LowContrast, ContentItemStatus.SpecAssembly);

        var action = OrchestratorCore.Decide(
            Context(Run(), ContentItemStatus.Validating, qaReport: Report(finding), previous: previous));

        action.Remediation!.Rung.ShouldBe(2);
    }

    [Fact]
    public void The_ladders_last_rung_falls_back_to_safe_mode_through_the_core_loop()
    {
        var run = Run(TenantLimits.Default with { MaxQualityAttemptsPerItem = 3 });
        run.RecordQualityAttempt();
        run.RecordQualityAttempt();

        var finding = new QaFinding { Code = QaFindingCode.LowContrast, Severity = QaSeverity.Blocking };

        // Two attempts already spent, one left: the router's ladder puts this at its final
        // rung — SafeMode — rather than a third try at the code's own step. The core loop
        // defers entirely to that policy rather than re-deciding it.
        var action = OrchestratorCore.Decide(
            Context(run, ContentItemStatus.Validating, qaReport: Report(finding)));

        action.Kind.ShouldBe(NextActionKind.Remediate);
        action.Remediation!.Outcome.ShouldBe(RemediationOutcome.SafeModeFallback);
    }

    [Fact]
    public void Exhausted_quality_attempts_reach_human_review_through_the_router()
    {
        var run = Run(TenantLimits.Default with { MaxQualityAttemptsPerItem = 3 });
        run.RecordQualityAttempt();
        run.RecordQualityAttempt();
        run.RecordQualityAttempt();

        var finding = new QaFinding { Code = QaFindingCode.LowContrast, Severity = QaSeverity.Blocking };

        // Every rung, including the SafeMode fallback, is already spent — the core loop
        // reports whatever the router decided without adding its own opinion.
        var action = OrchestratorCore.Decide(
            Context(run, ContentItemStatus.Validating, qaReport: Report(finding)));

        action.Kind.ShouldBe(NextActionKind.NeedsHumanReview);
    }

    [Fact]
    public void Repetitive_content_escalates_to_a_replan_rather_than_a_restart()
    {
        var finding = new QaFinding { Code = QaFindingCode.RepetitiveContent, Severity = QaSeverity.Major };

        var action = OrchestratorCore.Decide(
            Context(Run(), ContentItemStatus.Validating, qaReport: Report(finding)));

        action.Kind.ShouldBe(NextActionKind.Replan);
        action.Step.ShouldBeNull();
    }

    [Fact]
    public void An_only_minor_finding_never_produces_a_non_pass_report_to_begin_with()
    {
        // The safety net in DecideAfterValidation (a non-pass report with no Major-or-above
        // finding to route on) is defensive rather than reachable: QaReport.Outcome is
        // derived from the worst finding's severity, so Minor-only findings always pass.
        var report = Report(new QaFinding { Code = QaFindingCode.ShrinkToFitAbused, Severity = QaSeverity.Minor });

        report.Outcome.ShouldBe(QaOutcome.Pass);
        OrchestratorCore.Decide(Context(Run(), ContentItemStatus.Validating, qaReport: report))
            .Kind.ShouldBe(NextActionKind.Complete);
    }

    [Fact]
    public void A_deterministic_gate_finding_never_reaches_the_router_unroutable()
    {
        // Every QaFindingCode is asserted routable in RemediationRouterTests; this confirms
        // the core loop's null-finding branch is truly defensive rather than reachable
        // through any real DeterministicQaSuite output.
        foreach (var code in Enum.GetValues<QaFindingCode>())
        {
            var finding = new QaFinding { Code = code, Severity = QaSeverity.Blocking };
            var report = Report(finding);

            if (report.Outcome == QaOutcome.Pass)
            {
                continue;
            }

            var action = OrchestratorCore.Decide(Context(Run(), ContentItemStatus.Validating, qaReport: report));

            action.Kind.ShouldNotBe(NextActionKind.NeedsHumanReview, $"{code} produced an unroutable non-pass report.");
        }
    }
}
