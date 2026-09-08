using ContentPilot.Domain.Common;

namespace ContentPilot.Domain.Content;

/// <summary>
/// What this brand has already said. Written when an item is approved — never when it is
/// merely generated, because content that failed QA should not block future topics.
/// <para>
/// Deliberately denormalised: the strategist's context is one indexed query, and the
/// novelty check compares hashes rather than re-reading campaigns.
/// </para>
/// </summary>
public sealed class ContentHistoryEntry : Entity, ITenantOwned, IAppendOnly
{
    private ContentHistoryEntry()
    {
        Topic = null!;
        Pillar = null!;
        Hook = null!;
    }

    public ContentHistoryEntry(
        Guid tenantId,
        Guid brandId,
        Guid contentItemId,
        ContentItemType type,
        string topic,
        string pillar,
        string hook,
        long topicSimHash,
        long hookSimHash,
        string? templateId,
        DateTimeOffset approvedAt)
    {
        TenantId = Guard.NotEmpty(tenantId);
        BrandId = Guard.NotEmpty(brandId);
        ContentItemId = Guard.NotEmpty(contentItemId);
        Type = type;
        Topic = Guard.MaxLength(Guard.NotBlank(topic), 300);
        Pillar = Guard.MaxLength(Guard.NotBlank(pillar), 80);
        Hook = Guard.MaxLength(Guard.NotBlank(hook), 500);
        TopicSimHash = topicSimHash;
        HookSimHash = hookSimHash;
        TemplateId = templateId;
        ApprovedAt = approvedAt;
    }

    public Guid TenantId { get; private set; }

    public Guid BrandId { get; private set; }

    public Guid ContentItemId { get; private set; }

    public ContentItemType Type { get; private set; }

    public string Topic { get; private set; }

    public string Pillar { get; private set; }

    public string Hook { get; private set; }

    /// <summary>
    /// SimHash over normalised topic tokens. Near-duplicates land within a small Hamming
    /// distance, which is what makes "do not repeat the last four weeks" a computation
    /// rather than a request the model may quietly ignore.
    /// </summary>
    public long TopicSimHash { get; private set; }

    public long HookSimHash { get; private set; }

    public string? TemplateId { get; private set; }

    public double? QualityScore { get; private set; }

    /// <summary>
    /// The operator's own 1-5 rating. The single most useful signal in the system, and the
    /// only one that answers whether any of this is working.
    /// </summary>
    public int? HumanRating { get; private set; }

    public DateTimeOffset ApprovedAt { get; private set; }

    /// <summary>Set by the operator, or by the platform once direct publishing exists.</summary>
    public DateTimeOffset? PublishedAt { get; private set; }

    public void SetQualityScore(double score) => QualityScore = score;

    public void Rate(int rating) => HumanRating = Guard.InRange(rating, 1, 5);

    public void MarkPublished(DateTimeOffset when) => PublishedAt = when;
}
