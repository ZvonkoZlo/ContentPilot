using ContentPilot.Application.Abstractions;

namespace ContentPilot.Application.Jobs;

/// <summary>
/// Fires roughly hourly and re-enqueues itself. §21's own recommendation: firing hourly and
/// having each brand checked against its own IANA timezone is simpler and more robust than
/// building one cron expression per brand's local 06:00 Monday.
/// </summary>
[JobType("campaign-trigger-scan")]
public sealed record CampaignTriggerScanPayload;
