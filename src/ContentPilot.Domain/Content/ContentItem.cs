using ContentPilot.Domain.Common;

namespace ContentPilot.Domain.Content;

/// <summary>
/// One deliverable: a post, a carousel, a reel. This is the unit of work, of retry, and of
/// budget — items are independent, so one bad reel cannot block a good carousel.
/// </summary>
public sealed class ContentItem : Entity, ITenantOwned, IAuditable
{
    private ContentItem()
    {
        Topic = null!;
        Pillar = null!;
        Objective = null!;
    }

    public ContentItem(
        Guid tenantId,
        Guid campaignId,
        ContentItemType type,
        string topic,
        string pillar,
        string objective,
        DayOfWeek publishDay,
        int ordinal)
    {
        TenantId = Guard.NotEmpty(tenantId);
        CampaignId = Guard.NotEmpty(campaignId);
        Type = type;
        Topic = Guard.MaxLength(Guard.NotBlank(topic), 300);
        Pillar = Guard.MaxLength(Guard.NotBlank(pillar), 80);
        Objective = Guard.MaxLength(Guard.NotBlank(objective), 300);
        PublishDay = publishDay;
        Ordinal = ordinal;
        Status = ContentItemStatus.Pending;
    }

    public Guid TenantId { get; private set; }

    public Guid CampaignId { get; private set; }

    public ContentItemType Type { get; private set; }

    public string Topic { get; private set; }

    /// <summary>The content pillar: problem-solution, social-proof, feature-highlight.</summary>
    public string Pillar { get; private set; }

    public string Objective { get; private set; }

    public DayOfWeek PublishDay { get; private set; }

    /// <summary>Stable ordering within the campaign, assigned at planning time.</summary>
    public int Ordinal { get; private set; }

    public ContentItemStatus Status { get; private set; }

    /// <summary>
    /// Quality attempts only. Schema-repair and transient retries are counted separately and
    /// deliberately do not consume this budget — a provider 429 is not a quality failure.
    /// </summary>
    public int QualityAttempts { get; private set; }

    /// <summary>Total steps executed. A hard ceiling regardless of what the counters say.</summary>
    public int StepsExecuted { get; private set; }

    /// <summary>
    /// Set when an item runs out of attempts or budget. The best attempt so far is promoted
    /// rather than discarded — work is never thrown away.
    /// </summary>
    public Guid? BestAssetId { get; private set; }

    public string? RepetitionRisk { get; private set; }

    public string? FailureReason { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }

    public bool IsTerminal => Status is ContentItemStatus.Approved or ContentItemStatus.NeedsHumanReview or ContentItemStatus.Failed;

    public void MoveTo(ContentItemStatus next)
    {
        if (IsTerminal && next != ContentItemStatus.Approved)
        {
            throw new InvalidOperationException($"Item {Id} is already terminal at {Status}.");
        }

        Status = next;
    }

    public void RecordStep() => StepsExecuted++;

    public void RecordQualityAttempt() => QualityAttempts++;

    public void PromoteBestAttempt(Guid assetId) => BestAssetId = assetId;

    public void FlagRepetitionRisk(string reason) =>
        RepetitionRisk = Guard.MaxLength(Guard.NotBlank(reason), 500);

    public void SendToHumanReview(string reason)
    {
        FailureReason = Guard.MaxLength(Guard.NotBlank(reason), 1000);
        Status = ContentItemStatus.NeedsHumanReview;
    }

    public void Fail(string reason)
    {
        FailureReason = Guard.MaxLength(Guard.NotBlank(reason), 1000);
        Status = ContentItemStatus.Failed;
    }

    public void Approve() => Status = ContentItemStatus.Approved;
}

public enum ContentItemType
{
    StaticPost = 0,
    Carousel = 1,
    Reel = 2,
}

public enum ContentItemStatus
{
    Pending = 0,
    Directing = 1,
    Writing = 2,
    SpecAssembly = 3,
    AssetGeneration = 4,
    Rendering = 5,
    Validating = 6,
    Remediating = 7,
    Approved = 8,

    /// <summary>Not a failure. The best attempt is retained and shown with its findings.</summary>
    NeedsHumanReview = 9,
    Failed = 10,
}
