using ContentPilot.Application.Abstractions;
using ContentPilot.Application.Campaigns;
using ContentPilot.Application.Jobs;
using ContentPilot.Domain.Branding;
using ContentPilot.Domain.Content;
using ContentPilot.Infrastructure.Campaigns;
using ContentPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ContentPilot.Infrastructure.Jobs;

/// <summary>
/// §21's daily reconciler: "for each active brand, does a campaign exist for the current
/// week? If not, start one." The safety net for a scheduled trigger missed outright — the
/// worker was down at exactly 06:00 Monday local, a deploy landed in the wrong hour, a
/// timezone database was stale — and, unchanged, an operator's manual disaster recovery:
/// running this by hand catches up every brand that fell behind in one pass.
/// </summary>
public sealed class CampaignTriggerReconcileJobHandler(
    AppDbContext db,
    IJobQueue jobs,
    IClock clock,
    IMutableTenantContext tenantContext,
    CampaignStarter starter,
    ILogger<CampaignTriggerReconcileJobHandler> logger)
    : JobHandler<CampaignTriggerReconcilePayload>
{
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromDays(1);

    protected override async Task HandleAsync(JobExecutionContext context, CampaignTriggerReconcilePayload payload, CancellationToken ct)
    {
        var now = clock.UtcNow;

        List<Brand> brands;

        using (tenantContext.BeginCrossTenantScope())
        {
            brands = await db.Brands.Where(b => b.IsActive).ToListAsync(ct);
        }

        var started = 0;

        foreach (var brand in brands)
        {
            tenantContext.SetTenant(brand.TenantId);

            var local = TimeZoneInfoOrUtc(brand.TimeZoneId);
            var weekStart = CampaignWeek.MondayOnOrBefore(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, local).Date));

            try
            {
                var campaign = await starter.StartAsync(brand, weekStart, CampaignTrigger.Scheduled, ct);

                if (campaign is not null)
                {
                    await db.SaveChangesAsync(ct);
                    started++;
                    logger.LogWarning(
                        "Reconciler started campaign {CampaignId} for brand {BrandId} — the scheduled trigger missed the week of {WeekStart}.",
                        campaign.Id, brand.Id, weekStart);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Reconciler failed for brand {BrandId}.", brand.Id);
            }
        }

        if (started > 0)
        {
            logger.LogWarning("Reconciler started {Count} campaign(s) the scheduled trigger missed.", started);
        }

        await jobs.EnqueueAsync(new CampaignTriggerReconcilePayload(), tenantId: null, runAt: now.Add(ReconcileInterval), ct: ct);
        await db.SaveChangesAsync(ct);
    }

    private static TimeZoneInfo TimeZoneInfoOrUtc(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
    }
}
