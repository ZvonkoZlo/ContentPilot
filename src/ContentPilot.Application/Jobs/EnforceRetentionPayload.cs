using ContentPilot.Application.Abstractions;

namespace ContentPilot.Application.Jobs;

/// <summary>
/// Fires once daily and re-enqueues itself. §11's per-tenant retention policy, enforced: an
/// attempt's rendered bytes past <c>TenantLimits.AttemptArtefactRetentionDays</c>, and an
/// archived model prompt/response past <c>TenantLimits.ModelPayloadRetentionDays</c>.
/// </summary>
[JobType("enforce-retention")]
public sealed record EnforceRetentionPayload;
