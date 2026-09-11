using ContentPilot.Application.Orchestration;
using ContentPilot.Domain.Content;
using Shouldly;

namespace ContentPilot.UnitTests.Orchestration;

/// <summary>
/// The legality of every transition in §7, asserted directly rather than inferred from a
/// running pipeline — this is the table the orchestrator's "illegal transition is a bug"
/// promise depends on.
/// </summary>
public sealed class ItemStateMachineTests
{
    [Theory]
    [InlineData(ContentItemStatus.Pending, ContentItemStatus.Directing)]
    [InlineData(ContentItemStatus.Directing, ContentItemStatus.Writing)]
    [InlineData(ContentItemStatus.Writing, ContentItemStatus.SpecAssembly)]
    [InlineData(ContentItemStatus.SpecAssembly, ContentItemStatus.AssetGeneration)]
    [InlineData(ContentItemStatus.AssetGeneration, ContentItemStatus.Rendering)]
    [InlineData(ContentItemStatus.Rendering, ContentItemStatus.Validating)]
    [InlineData(ContentItemStatus.Validating, ContentItemStatus.Approved)]
    public void The_happy_path_advances_one_step_at_a_time(ContentItemStatus from, ContentItemStatus to)
    {
        ItemStateMachine.IsLegalTransition(from, to).ShouldBeTrue();
        ItemStateMachine.NextStep(from).ShouldBe(to);
    }

    [Fact]
    public void Skipping_a_step_is_illegal()
    {
        // Skipping SpecAssembly would mean a render happened against a spec nobody
        // assembled. That is not a runtime condition; it is a bug in whatever proposed it.
        ItemStateMachine.IsLegalTransition(ContentItemStatus.Writing, ContentItemStatus.AssetGeneration).ShouldBeFalse();
    }

    [Fact]
    public void Moving_backward_on_the_happy_path_directly_is_illegal()
    {
        // A restart must go through Remediating, which is what lets the orchestrator record
        // why the attempt is being spent.
        ItemStateMachine.IsLegalTransition(ContentItemStatus.Rendering, ContentItemStatus.Writing).ShouldBeFalse();
    }

    [Theory]
    [InlineData(ContentItemStatus.Pending)]
    [InlineData(ContentItemStatus.Directing)]
    [InlineData(ContentItemStatus.Writing)]
    [InlineData(ContentItemStatus.SpecAssembly)]
    [InlineData(ContentItemStatus.AssetGeneration)]
    [InlineData(ContentItemStatus.Rendering)]
    [InlineData(ContentItemStatus.Validating)]
    public void Every_non_terminal_step_can_reach_remediation_or_a_review_sink(ContentItemStatus from)
    {
        ItemStateMachine.IsLegalTransition(from, ContentItemStatus.Remediating).ShouldBeTrue();
        ItemStateMachine.IsLegalTransition(from, ContentItemStatus.NeedsHumanReview).ShouldBeTrue();
        ItemStateMachine.IsLegalTransition(from, ContentItemStatus.Failed).ShouldBeTrue();
    }

    [Fact]
    public void Approved_is_terminal_on_the_happy_path()
    {
        ItemStateMachine.IsLegalTransition(ContentItemStatus.Approved, ContentItemStatus.Remediating).ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => ItemStateMachine.NextStep(ContentItemStatus.Approved));
    }

    [Theory]
    [InlineData(ContentItemStatus.Pending)]
    [InlineData(ContentItemStatus.Directing)]
    [InlineData(ContentItemStatus.Writing)]
    [InlineData(ContentItemStatus.SpecAssembly)]
    [InlineData(ContentItemStatus.AssetGeneration)]
    [InlineData(ContentItemStatus.Rendering)]
    [InlineData(ContentItemStatus.Validating)]
    public void Remediating_can_restart_at_any_earlier_or_equal_step(ContentItemStatus target)
    {
        ItemStateMachine.IsLegalTransition(ContentItemStatus.Remediating, target).ShouldBeTrue();
    }

    [Fact]
    public void Remediating_cannot_restart_at_approved()
    {
        // Approved is a destination reached by validating cleanly, never a restart target.
        ItemStateMachine.IsLegalTransition(ContentItemStatus.Remediating, ContentItemStatus.Approved).ShouldBeFalse();
    }

    [Fact]
    public void A_state_cannot_transition_to_itself()
    {
        ItemStateMachine.IsLegalTransition(ContentItemStatus.Writing, ContentItemStatus.Writing).ShouldBeFalse();
    }

    [Theory]
    [InlineData(ContentItemStatus.Rendering, ContentItemStatus.Writing, true)]
    [InlineData(ContentItemStatus.Writing, ContentItemStatus.Rendering, false)]
    [InlineData(ContentItemStatus.Rendering, ContentItemStatus.Rendering, true)]
    public void A_remediation_target_can_never_sit_later_than_the_step_that_raised_the_finding(
        ContentItemStatus sourceStep, ContentItemStatus target, bool expectedValid)
    {
        // Restarting later than the step that produced the finding would be a cycle with no
        // state change — the loop-safety rule from §8. The step that raised the finding
        // itself is a legitimate target: a logo fix found at Rendering restarts there.
        ItemStateMachine.IsValidRemediationTarget(sourceStep, target).ShouldBe(expectedValid);
    }

    [Fact]
    public void A_remediation_target_cannot_be_approved()
    {
        ItemStateMachine.IsValidRemediationTarget(ContentItemStatus.Validating, ContentItemStatus.Approved).ShouldBeFalse();
    }
}
