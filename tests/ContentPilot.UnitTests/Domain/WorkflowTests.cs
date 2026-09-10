using ContentPilot.Domain.Content;
using ContentPilot.Domain.Tenancy;
using ContentPilot.Domain.Workflow;
using Shouldly;

namespace ContentPilot.UnitTests.Domain;

public sealed class WorkflowRunTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 8, 0, 0, TimeSpan.Zero);

    private static WorkflowRun Run(TenantLimits? limits = null) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), WorkflowScope.Item, Guid.CreateVersion7(), Now, limits);

    [Fact]
    public void A_new_run_takes_its_ceilings_from_the_tenants_limits()
    {
        // The plan is explicit: tuning these is data, never a deploy. A hard-coded ceiling
        // on the run itself would silently ignore a tenant override.
        var limits = TenantLimits.Default with { MaxQualityAttemptsPerItem = 5, MaxStepsPerItem = 10 };
        var run = Run(limits);

        run.MaxQualityAttempts.ShouldBe(5);
        run.MaxStepsExecuted.ShouldBe(10);
        run.Deadline.ShouldBe(Now + limits.MaxRunDuration);
        run.State.ShouldBe(WorkflowRunState.Active);
    }

    [Fact]
    public void Counters_start_at_zero_and_exhaustion_is_false()
    {
        var run = Run();

        run.QualityAttempts.ShouldBe(0);
        run.QualityExhausted.ShouldBeFalse();
        run.SchemaRepairExhausted.ShouldBeFalse();
        run.TransientExhausted.ShouldBeFalse();
        run.StepBudgetExhausted.ShouldBeFalse();
    }

    [Fact]
    public void Quality_attempts_exhaust_at_the_tenants_ceiling()
    {
        var run = Run(TenantLimits.Default with { MaxQualityAttemptsPerItem = 2 });

        run.RecordQualityAttempt();
        run.QualityExhausted.ShouldBeFalse();

        run.RecordQualityAttempt();
        run.QualityExhausted.ShouldBeTrue();
    }

    [Fact]
    public void Schema_repair_and_transient_counters_are_independent_of_quality()
    {
        var run = Run();

        run.RecordSchemaRepair();
        run.RecordTransientRetry();

        run.QualityAttempts.ShouldBe(0);
        run.SchemaRepairAttempts.ShouldBe(1);
        run.TransientAttempts.ShouldBe(1);
    }

    [Fact]
    public void The_step_cap_is_absolute_regardless_of_other_counters()
    {
        var run = Run(TenantLimits.Default with { MaxStepsPerItem = 2 });

        run.RecordStep();
        run.StepBudgetExhausted.ShouldBeFalse();

        run.RecordStep();
        run.StepBudgetExhausted.ShouldBeTrue();
    }

    [Fact]
    public void Past_the_deadline_is_detected_from_wall_clock_time()
    {
        var run = Run(TenantLimits.Default with { MaxRunDuration = TimeSpan.FromMinutes(45) });

        run.IsPastDeadline(Now + TimeSpan.FromMinutes(44)).ShouldBeFalse();
        run.IsPastDeadline(Now + TimeSpan.FromMinutes(45)).ShouldBeTrue();
    }

    [Fact]
    public void A_lease_held_by_someone_else_and_not_expired_is_refused()
    {
        var run = Run();
        run.Lease("worker-a", Now, TimeSpan.FromMinutes(1));

        Should.Throw<InvalidOperationException>(() => run.Lease("worker-b", Now, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void The_same_owner_can_renew_its_own_lease()
    {
        var run = Run();
        run.Lease("worker-a", Now, TimeSpan.FromMinutes(1));

        Should.NotThrow(() => run.Lease("worker-a", Now, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void An_expired_lease_can_be_taken_by_another_worker()
    {
        var run = Run();
        run.Lease("worker-a", Now, TimeSpan.FromSeconds(30));

        Should.NotThrow(() => run.Lease("worker-b", Now + TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void A_terminal_run_cannot_be_leased()
    {
        var run = Run();
        run.Complete(Now);

        Should.Throw<InvalidOperationException>(() => run.Lease("worker-a", Now, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void Completing_a_run_releases_its_lease()
    {
        var run = Run();
        run.Lease("worker-a", Now, TimeSpan.FromMinutes(1));

        run.Complete(Now);

        run.LeaseOwner.ShouldBeNull();
        run.LeaseUntil.ShouldBeNull();
        run.IsTerminal.ShouldBeTrue();
    }

    [Fact]
    public void Sending_to_human_review_is_not_the_same_terminal_state_as_failing()
    {
        var run = Run();

        run.SendToHumanReview("quality attempts exhausted", Now);

        run.State.ShouldBe(WorkflowRunState.NeedsHumanReview);
        run.IsTerminal.ShouldBeTrue();
        run.LastError.ShouldBe("quality attempts exhausted");
    }
}

public sealed class WorkflowStepTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_idempotency_key_is_composed_from_run_step_and_attempt()
    {
        var runId = Guid.CreateVersion7();
        var step = new WorkflowStep(Guid.CreateVersion7(), runId, "Writing", 2, Now);

        step.IdempotencyKey.ShouldBe(WorkflowStep.IdempotencyKeyFor(runId, "Writing", 2));
        step.IdempotencyKey.ShouldContain("Writing");
        step.IdempotencyKey.ShouldContain(":2");
    }

    [Fact]
    public void Two_different_attempts_of_the_same_step_get_different_keys()
    {
        var runId = Guid.CreateVersion7();

        var first = new WorkflowStep(Guid.CreateVersion7(), runId, "Rendering", 1, Now);
        var second = new WorkflowStep(Guid.CreateVersion7(), runId, "Rendering", 2, Now);

        first.IdempotencyKey.ShouldNotBe(second.IdempotencyKey);
    }

    [Fact]
    public void A_step_starts_running_and_can_succeed_or_fail()
    {
        var step = new WorkflowStep(Guid.CreateVersion7(), Guid.CreateVersion7(), "Directing", 1, Now);
        step.Outcome.ShouldBe(WorkflowStepOutcome.Running);

        var agentRunId = Guid.CreateVersion7();
        step.Succeed(Now, agentRunId);

        step.Outcome.ShouldBe(WorkflowStepOutcome.Succeeded);
        step.AgentRunId.ShouldBe(agentRunId);
        step.CompletedAt.ShouldBe(Now);
    }

    [Fact]
    public void A_failed_step_records_its_error()
    {
        var step = new WorkflowStep(Guid.CreateVersion7(), Guid.CreateVersion7(), "Rendering", 1, Now);

        step.Fail("renderer timed out", Now);

        step.Outcome.ShouldBe(WorkflowStepOutcome.Failed);
        step.Error.ShouldBe("renderer timed out");
    }
}

public sealed class ContentRevisionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_revision_records_why_an_attempt_changed()
    {
        var revision = new ContentRevision(
            Guid.CreateVersion7(), Guid.CreateVersion7(), attempt: 2,
            reason: "TextOverflow at Validating",
            remediationAction: "Restart at Writing",
            createdAt: Now,
            restartAtStep: nameof(ContentItemStatus.Writing));

        revision.Attempt.ShouldBe(2);
        revision.RestartAtStep.ShouldBe("Writing");
    }

    [Fact]
    public void A_terminal_outcome_has_no_restart_step()
    {
        var revision = new ContentRevision(
            Guid.CreateVersion7(), Guid.CreateVersion7(), attempt: 3,
            reason: "quality attempts exhausted",
            remediationAction: "NeedsHumanReview",
            createdAt: Now);

        revision.RestartAtStep.ShouldBeNull();
    }
}

public sealed class BudgetReservationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_fresh_reservation_is_live()
    {
        var reservation = new BudgetReservation(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), 10_000, Now);

        reservation.IsLive(Now).ShouldBeTrue();
    }

    [Fact]
    public void A_released_reservation_is_no_longer_live()
    {
        var reservation = new BudgetReservation(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), 10_000, Now);

        reservation.Release(Now);

        reservation.IsLive(Now).ShouldBeFalse();
    }

    [Fact]
    public void An_expired_reservation_is_no_longer_live_even_if_never_released()
    {
        // A crashed step leaves a reservation nobody released. The TTL is what stops it
        // from permanently eating into the budget.
        var reservation = new BudgetReservation(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), 10_000, Now, TimeSpan.FromMinutes(5));

        reservation.IsLive(Now + TimeSpan.FromMinutes(5)).ShouldBeFalse();
    }

    [Fact]
    public void Releasing_twice_keeps_the_first_timestamp()
    {
        var reservation = new BudgetReservation(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), 10_000, Now);

        reservation.Release(Now);
        reservation.Release(Now + TimeSpan.FromMinutes(1));

        reservation.ReleasedAt.ShouldBe(Now);
    }

    [Fact]
    public void A_negative_estimate_is_refused()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new BudgetReservation(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), -1, Now));
    }
}
