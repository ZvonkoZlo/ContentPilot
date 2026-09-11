using ContentPilot.Application.Abstractions;

namespace ContentPilot.Application.Jobs;

/// <summary>
/// Fires once daily and re-enqueues itself. §21's safety net for the exact-hour trigger: "for
/// each active brand, does a campaign exist for the current week? If not, start one." Also
/// the manual disaster-recovery path if the scheduled trigger was ever missed outright.
/// </summary>
[JobType("campaign-trigger-reconcile")]
public sealed record CampaignTriggerReconcilePayload;
