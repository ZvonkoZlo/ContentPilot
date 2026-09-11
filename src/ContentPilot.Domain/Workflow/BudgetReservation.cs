using ContentPilot.Domain.Common;

namespace ContentPilot.Domain.Workflow;

/// <summary>
/// A provisional hold against a budget, taken before a billable call and released once its
/// real cost lands as a <c>CostEntry</c>. Reservations exist because parallel items would
/// otherwise each read the same "spent so far" figure and collectively blow the campaign
/// budget between the check and the write — this closes that race without a lock on the
/// whole campaign.
/// <para>
/// Short-lived by design: a reservation nobody released within its TTL is dead weight from
/// a crashed step, and is cleaned up by the same reaper that reclaims expired job leases.
/// </para>
/// </summary>
public sealed class BudgetReservation : Entity, ITenantOwned
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);

    private BudgetReservation()
    {
    }

    public BudgetReservation(
        Guid tenantId,
        Guid campaignId,
        Guid? contentItemId,
        long estimateMicroCents,
        DateTimeOffset reservedAt,
        TimeSpan? ttl = null)
    {
        TenantId = Guard.NotEmpty(tenantId);
        CampaignId = Guard.NotEmpty(campaignId);
        ContentItemId = contentItemId;
        EstimateMicroCents = estimateMicroCents >= 0
            ? estimateMicroCents
            : throw new ArgumentOutOfRangeException(nameof(estimateMicroCents), estimateMicroCents, "An estimate cannot be negative.");
        ReservedAt = reservedAt;
        ExpiresAt = reservedAt + (ttl ?? DefaultTtl);
    }

    public Guid TenantId { get; private set; }

    public Guid CampaignId { get; private set; }

    /// <summary>Null for campaign-level spend such as planning.</summary>
    public Guid? ContentItemId { get; private set; }

    public long EstimateMicroCents { get; private set; }

    public DateTimeOffset ReservedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? ReleasedAt { get; private set; }

    /// <summary>Live means it still counts against the budget: not released, not expired.</summary>
    public bool IsLive(DateTimeOffset now) => ReleasedAt is null && now < ExpiresAt;

    /// <summary>Called once the real cost has been written as a <see cref="Observability.CostEntry"/>.</summary>
    public void Release(DateTimeOffset now) => ReleasedAt ??= now;
}
