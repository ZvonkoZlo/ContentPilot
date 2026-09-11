using ContentPilot.Application.Orchestration;
using Shouldly;

namespace ContentPilot.UnitTests.Orchestration;

/// <summary>
/// The reserve-then-commit arithmetic from §24, isolated from Postgres entirely: every
/// number here is a sum the caller would otherwise have computed from the ledger.
/// </summary>
public sealed class BudgetGuardTests
{
    [Fact]
    public void A_call_that_fits_under_the_limit_is_allowed()
    {
        var decision = BudgetGuard.CheckItem(spentMicroCents: 20_000, reservedMicroCents: 5_000, estimateMicroCents: 10_000, itemLimitMicroCents: 60_000);

        decision.Allowed.ShouldBeTrue();
        decision.ProjectedMicroCents.ShouldBe(35_000);
    }

    [Fact]
    public void A_call_that_would_exceed_the_limit_is_refused()
    {
        var decision = BudgetGuard.CheckItem(spentMicroCents: 55_000, reservedMicroCents: 0, estimateMicroCents: 10_000, itemLimitMicroCents: 60_000);

        decision.Allowed.ShouldBeFalse();
        decision.ProjectedMicroCents.ShouldBe(65_000);
    }

    [Fact]
    public void Landing_exactly_on_the_limit_is_allowed()
    {
        var decision = BudgetGuard.CheckItem(spentMicroCents: 50_000, reservedMicroCents: 0, estimateMicroCents: 10_000, itemLimitMicroCents: 60_000);

        decision.Allowed.ShouldBeTrue();
    }

    [Fact]
    public void Other_reservations_in_flight_count_against_the_same_limit()
    {
        // The whole reason reservations exist: two parallel items would otherwise both read
        // "spent so far" as the same figure and collectively blow the budget.
        var decision = BudgetGuard.CheckItem(spentMicroCents: 10_000, reservedMicroCents: 45_000, estimateMicroCents: 10_000, itemLimitMicroCents: 60_000);

        decision.Allowed.ShouldBeFalse();
    }

    [Fact]
    public void A_negative_estimate_is_refused_outright()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            BudgetGuard.CheckItem(spentMicroCents: 0, reservedMicroCents: 0, estimateMicroCents: -1, itemLimitMicroCents: 60_000));
    }

    [Fact]
    public void An_item_within_its_own_limit_can_still_be_refused_by_the_campaign_ceiling()
    {
        var decision = BudgetGuard.CheckBoth(
            itemSpentMicroCents: 10_000, itemReservedMicroCents: 0, itemLimitMicroCents: 60_000,
            campaignSpentMicroCents: 595_000, campaignReservedMicroCents: 0, campaignLimitMicroCents: 600_000,
            estimateMicroCents: 10_000);

        decision.Allowed.ShouldBeFalse();
        decision.Scope.ShouldBe(BudgetScope.Campaign);
    }

    [Fact]
    public void The_item_ceiling_is_reported_when_both_would_be_breached()
    {
        // The item limit is the more specific, more actionable fact — reported first even
        // when the campaign is also out of room.
        var decision = BudgetGuard.CheckBoth(
            itemSpentMicroCents: 55_000, itemReservedMicroCents: 0, itemLimitMicroCents: 60_000,
            campaignSpentMicroCents: 595_000, campaignReservedMicroCents: 0, campaignLimitMicroCents: 600_000,
            estimateMicroCents: 10_000);

        decision.Allowed.ShouldBeFalse();
        decision.Scope.ShouldBe(BudgetScope.Item);
    }

    [Fact]
    public void Both_scopes_pass_when_there_is_room_in_each()
    {
        var decision = BudgetGuard.CheckBoth(
            itemSpentMicroCents: 10_000, itemReservedMicroCents: 0, itemLimitMicroCents: 60_000,
            campaignSpentMicroCents: 100_000, campaignReservedMicroCents: 0, campaignLimitMicroCents: 600_000,
            estimateMicroCents: 10_000);

        decision.Allowed.ShouldBeTrue();
        decision.Scope.ShouldBe(BudgetScope.Campaign);
    }
}
