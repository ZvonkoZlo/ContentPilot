namespace ContentPilot.Application.Orchestration;

/// <summary>
/// Reserve, then commit, from §24. A pure decision over numbers the caller already summed
/// from the ledger — this type never touches the database itself, so the reservation race
/// it closes can be unit-tested without Postgres: parallel items would otherwise each read
/// the same "spent so far" figure and collectively blow the campaign budget between the
/// check and the write.
/// <para>
/// Two scopes are checked together because they are the same rule at different radii: an
/// item can be individually affordable and still be refused for pushing the campaign over
/// its own ceiling.
/// </para>
/// </summary>
public static class BudgetGuard
{
    /// <summary>
    /// <paramref name="spentMicroCents"/> and <paramref name="reservedMicroCents"/> are sums
    /// the caller already computed — actual <c>CostEntry</c> rows, and live
    /// <c>BudgetReservation</c> rows respectively, both scoped to the same item or campaign.
    /// </summary>
    public static BudgetDecision CheckItem(
        long spentMicroCents,
        long reservedMicroCents,
        long estimateMicroCents,
        long itemLimitMicroCents) =>
        Check(spentMicroCents, reservedMicroCents, estimateMicroCents, itemLimitMicroCents, BudgetScope.Item);

    public static BudgetDecision CheckCampaign(
        long spentMicroCents,
        long reservedMicroCents,
        long estimateMicroCents,
        long campaignLimitMicroCents) =>
        Check(spentMicroCents, reservedMicroCents, estimateMicroCents, campaignLimitMicroCents, BudgetScope.Campaign);

    /// <summary>
    /// Checks the item ceiling, then the campaign ceiling with the same call's estimate
    /// added at both radii. The first breach wins — an item over its own limit is reported
    /// as an item breach even if the campaign also has no room left, because the item limit
    /// is the more specific, more actionable fact.
    /// </summary>
    public static BudgetDecision CheckBoth(
        long itemSpentMicroCents,
        long itemReservedMicroCents,
        long itemLimitMicroCents,
        long campaignSpentMicroCents,
        long campaignReservedMicroCents,
        long campaignLimitMicroCents,
        long estimateMicroCents)
    {
        var itemDecision = CheckItem(itemSpentMicroCents, itemReservedMicroCents, estimateMicroCents, itemLimitMicroCents);

        return itemDecision.Allowed
            ? CheckCampaign(campaignSpentMicroCents, campaignReservedMicroCents, estimateMicroCents, campaignLimitMicroCents)
            : itemDecision;
    }

    private static BudgetDecision Check(
        long spentMicroCents,
        long reservedMicroCents,
        long estimateMicroCents,
        long limitMicroCents,
        BudgetScope scope)
    {
        if (estimateMicroCents < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(estimateMicroCents), estimateMicroCents, "An estimate cannot be negative.");
        }

        var projected = spentMicroCents + reservedMicroCents + estimateMicroCents;

        return projected > limitMicroCents
            ? BudgetDecision.Exceeded(scope, projected, limitMicroCents)
            : BudgetDecision.Allow(scope, projected, limitMicroCents);
    }
}

public enum BudgetScope
{
    Item,
    Campaign,
}

public sealed record BudgetDecision
{
    public required bool Allowed { get; init; }

    public required BudgetScope Scope { get; init; }

    /// <summary>Spent + reserved + this call's estimate — what committing would bring the total to.</summary>
    public required long ProjectedMicroCents { get; init; }

    public required long LimitMicroCents { get; init; }

    public static BudgetDecision Allow(BudgetScope scope, long projected, long limit) => new()
    {
        Allowed = true,
        Scope = scope,
        ProjectedMicroCents = projected,
        LimitMicroCents = limit,
    };

    public static BudgetDecision Exceeded(BudgetScope scope, long projected, long limit) => new()
    {
        Allowed = false,
        Scope = scope,
        ProjectedMicroCents = projected,
        LimitMicroCents = limit,
    };
}
