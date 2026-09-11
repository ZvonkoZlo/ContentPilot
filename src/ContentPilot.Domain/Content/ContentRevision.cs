using ContentPilot.Domain.Common;

namespace ContentPilot.Domain.Content;

/// <summary>
/// The human-readable story of "why does attempt 3 exist". Append-only, one row per
/// remediation decision, written whenever an item's attempt changes for a reason worth
/// naming — a QA finding routed to an earlier step, a budget wall, an exhausted counter.
/// <para>
/// This is deliberately not derived from <c>QualityReview</c> rows at read time: a reviewer
/// debugging "why is this post mediocre" should not have to reconstruct the remediation
/// router's reasoning from raw findings, and a breach writes its own row precisely so that
/// "it hit a budget wall on attempt two" is the whole answer, not an inference.
/// </para>
/// </summary>
public sealed class ContentRevision : Entity, ITenantOwned, IAppendOnly
{
    private ContentRevision()
    {
        Reason = null!;
        RemediationAction = null!;
    }

    public ContentRevision(
        Guid tenantId,
        Guid contentItemId,
        int attempt,
        string reason,
        string remediationAction,
        DateTimeOffset createdAt,
        string? findingsJson = null,
        string? restartAtStep = null)
    {
        TenantId = Guard.NotEmpty(tenantId);
        ContentItemId = Guard.NotEmpty(contentItemId);
        Attempt = Guard.InRange(attempt, 1, 1000);
        Reason = Guard.MaxLength(Guard.NotBlank(reason), 1000);
        RemediationAction = Guard.MaxLength(Guard.NotBlank(remediationAction), 200);
        CreatedAt = createdAt;
        FindingsJson = findingsJson;
        RestartAtStep = restartAtStep is null ? null : Guard.MaxLength(restartAtStep, 80);
    }

    public Guid TenantId { get; private set; }

    public Guid ContentItemId { get; private set; }

    public int Attempt { get; private set; }

    /// <summary>Why this revision happened: a finding summary, "budget exhausted", "step cap hit".</summary>
    public string Reason { get; private set; }

    /// <summary>What the remediation router decided to do about it.</summary>
    public string RemediationAction { get; private set; }

    /// <summary>The findings that triggered this revision, verbatim, for the review UI.</summary>
    public string? FindingsJson { get; private set; }

    /// <summary>Null when the outcome was terminal (NeedsHumanReview, Failed) rather than a restart.</summary>
    public string? RestartAtStep { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
}
