using ContentPilot.Domain.Common;

namespace ContentPilot.Domain.Observability;

/// <summary>
/// One billable event. Append-only: budgets are sums over this table, and a ledger you can
/// edit is not a ledger.
/// <para>
/// The unit price is copied onto the row rather than looked up later. When prices change,
/// history must not silently rewrite itself — last month's campaign cost what it cost.
/// </para>
/// </summary>
public sealed class CostEntry : Entity, ITenantOwned, IAppendOnly
{
    private CostEntry()
    {
        Provider = null!;
        Resource = null!;
    }

    public CostEntry(
        Guid tenantId,
        Guid campaignId,
        Guid? contentItemId,
        Guid? agentRunId,
        CostKind kind,
        string provider,
        string resource,
        long units,
        long unitPriceMicroCentsPerMillion,
        long amountMicroCents,
        DateTimeOffset incurredAt)
    {
        TenantId = Guard.NotEmpty(tenantId);
        CampaignId = Guard.NotEmpty(campaignId);
        ContentItemId = contentItemId;
        AgentRunId = agentRunId;
        Kind = kind;
        Provider = Guard.MaxLength(Guard.NotBlank(provider), 60);
        Resource = Guard.MaxLength(Guard.NotBlank(resource), 120);
        Units = units;
        UnitPriceMicroCentsPerMillion = unitPriceMicroCentsPerMillion;
        AmountMicroCents = amountMicroCents;
        IncurredAt = incurredAt;
    }

    public Guid TenantId { get; private set; }

    public Guid CampaignId { get; private set; }

    /// <summary>Null for campaign-level spend such as planning.</summary>
    public Guid? ContentItemId { get; private set; }

    public Guid? AgentRunId { get; private set; }

    public CostKind Kind { get; private set; }

    public string Provider { get; private set; }

    /// <summary>The model id or service name the charge came from.</summary>
    public string Resource { get; private set; }

    /// <summary>Tokens, images, or seconds, depending on <see cref="Kind"/>.</summary>
    public long Units { get; private set; }

    /// <summary>Snapshotted so a later price change cannot rewrite this row's meaning.</summary>
    public long UnitPriceMicroCentsPerMillion { get; private set; }

    public long AmountMicroCents { get; private set; }

    public DateTimeOffset IncurredAt { get; private set; }

    public decimal AmountUsd => AmountMicroCents / 100_000_000m;
}

public enum CostKind
{
    InputTokens = 0,
    OutputTokens = 1,

    /// <summary>Cached reads are billed at a fraction of the input rate.</summary>
    CacheReadTokens = 2,
    CacheWriteTokens = 3,
    ImageGeneration = 4,
    RenderSeconds = 5,
}
