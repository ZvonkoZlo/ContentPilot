using ContentPilot.Application.Abstractions;

namespace ContentPilot.Application.Jobs;

/// <summary>
/// Advances one campaign: plans it, fans its items out, and later checks whether they have
/// all finished. Enqueued once when a campaign is triggered, and again by the handler
/// itself while items are still running — the campaign-level half of the same
/// "execute, persist, re-enqueue" shape <c>AdvanceContentItemWorkflowPayload</c> follows.
/// </summary>
[JobType("advance-campaign-workflow")]
public sealed record AdvanceCampaignWorkflowPayload(Guid CampaignId);
