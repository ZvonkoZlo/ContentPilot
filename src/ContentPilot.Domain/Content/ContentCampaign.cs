using ContentPilot.Domain.Common;

namespace ContentPilot.Domain.Content;

/// <summary>
/// One week of content for one brand. Every campaign pins the brand version it was planned
/// against, so a post can always be traced back to what the agents actually saw.
/// </summary>
public sealed class ContentCampaign : Entity, ITenantOwned, IAuditable
{
    private ContentCampaign()
    {
    }

    public ContentCampaign(Guid tenantId, Guid brandId, DateOnly weekStart, CampaignTrigger trigger, Guid brandProfileVersionId, long budgetMicroCents)
    {
        TenantId = Guard.NotEmpty(tenantId);
        BrandId = Guard.NotEmpty(brandId);
        WeekStart = weekStart;
        Trigger = trigger;
        BrandProfileVersionId = Guard.NotEmpty(brandProfileVersionId);
        BudgetMicroCents = budgetMicroCents;
        Status = CampaignStatus.Draft;
    }

    public Guid TenantId { get; private set; }

    public Guid BrandId { get; private set; }

    /// <summary>Monday of the week this content is for, in the brand's timezone.</summary>
    public DateOnly WeekStart { get; private set; }

    public CampaignTrigger Trigger { get; private set; }

    /// <summary>
    /// The frozen Brand Brain this campaign was planned from. An edit to the live profile
    /// after this point cannot change what a campaign in flight is working from.
    /// </summary>
    public Guid BrandProfileVersionId { get; private set; }

    public CampaignStatus Status { get; private set; }

    public string? Theme { get; private set; }

    public long BudgetMicroCents { get; private set; }

    public string? FailureReason { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }

    public bool IsTerminal => Status is CampaignStatus.Ready or CampaignStatus.PartiallyReady or CampaignStatus.Failed or CampaignStatus.Cancelled;

    public void BeginPlanning() => Transition(CampaignStatus.Planning);

    public void PlanAccepted(string theme)
    {
        Theme = Guard.MaxLength(Guard.NotBlank(theme), 300);
        Transition(CampaignStatus.ItemsRunning);
    }

    public void BeginPackaging() => Transition(CampaignStatus.Packaging);

    public void Complete(bool allApproved, DateTimeOffset now)
    {
        // PartiallyReady is a real outcome, not a failure. A week where three of five posts
        // are perfect is a good week; a week where two vanished is a broken product.
        Transition(allApproved ? CampaignStatus.Ready : CampaignStatus.PartiallyReady);
        CompletedAt = now;
    }

    public void Fail(string reason, DateTimeOffset now)
    {
        FailureReason = Guard.MaxLength(Guard.NotBlank(reason), 1000);
        Transition(CampaignStatus.Failed);
        CompletedAt = now;
    }

    public void Cancel(DateTimeOffset now)
    {
        Transition(CampaignStatus.Cancelled);
        CompletedAt = now;
    }

    /// <summary>
    /// An illegal transition is a bug in the orchestrator, not a runtime condition, so it
    /// throws rather than being absorbed.
    /// </summary>
    private void Transition(CampaignStatus next)
    {
        var legal = Status switch
        {
            CampaignStatus.Draft => next is CampaignStatus.Planning or CampaignStatus.Cancelled or CampaignStatus.Failed,
            CampaignStatus.Planning => next is CampaignStatus.ItemsRunning or CampaignStatus.Failed or CampaignStatus.Cancelled,
            CampaignStatus.ItemsRunning => next is CampaignStatus.Packaging or CampaignStatus.Failed or CampaignStatus.Cancelled,
            CampaignStatus.Packaging => next is CampaignStatus.Ready or CampaignStatus.PartiallyReady or CampaignStatus.Failed,
            _ => false,
        };

        if (!legal)
        {
            throw new InvalidOperationException($"A campaign cannot move from {Status} to {next}.");
        }

        Status = next;
    }
}

public enum CampaignStatus
{
    Draft = 0,
    Planning = 1,
    ItemsRunning = 2,
    Packaging = 3,
    Ready = 4,

    /// <summary>Some items are ready, some need a human. Still a usable week.</summary>
    PartiallyReady = 5,
    Failed = 6,
    Cancelled = 7,
}

public enum CampaignTrigger
{
    Manual = 0,
    Scheduled = 1,

    /// <summary>A product release or promotion. Not implemented yet; the hook exists.</summary>
    Event = 2,
}
