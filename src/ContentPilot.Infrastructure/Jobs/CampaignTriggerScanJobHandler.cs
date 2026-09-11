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
/// §21's scheduled trigger, built on the plan's own recommendation rather than one cron
/// expression per brand's timezone: fire roughly hourly, and for every active brand ask
/// "is it 06:00 on a Monday there, right now?" A brand whose local clock just crossed that
/// hour gets a campaign started; everyone else is skipped until the next pass.
/// <para>
/// Re-enqueues itself every time, the same self-scheduling shape the item and campaign
/// workflow jobs already use — there is no separate scheduler process, only this job
/// perpetually deciding when to run again.
/// </para>
/// </summary>
public sealed class CampaignTriggerScanJobHandler(
    AppDbContext db,
    IJobQueue jobs,
    IClock clock,
    IMutableTenantContext tenantContext,
    CampaignStarter starter,
    ILogger<CampaignTriggerScanJobHandler> logger)
    : JobHandler<CampaignTriggerScanPayload>
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromHours(1);

    /// <summary>Weekly generation fires at 06:00 local to the brand — see <c>Brand.TimeZoneId</c>'s own doc comment.</summary>
    private const int TriggerHour = 6;

    protected override async Task HandleAsync(JobExecutionContext context, CampaignTriggerScanPayload payload, CancellationToken ct)
    {
        var now = clock.UtcNow;

        List<Brand> brands;

        using (tenantContext.BeginCrossTenantScope())
        {
            brands = await db.Brands.Where(b => b.IsActive).ToListAsync(ct);
        }

        foreach (var brand in brands)
        {
            if (!IsTriggerMoment(brand.TimeZoneId, now))
            {
                continue;
            }

            tenantContext.SetTenant(brand.TenantId);

            var weekStart = CampaignWeek.MondayOnOrBefore(LocalDate(brand.TimeZoneId, now));

            try
            {
                var campaign = await starter.StartAsync(brand, weekStart, CampaignTrigger.Scheduled, ct);

                if (campaign is not null)
                {
                    await db.SaveChangesAsync(ct);
                    logger.LogInformation("Scheduled trigger started campaign {CampaignId} for brand {BrandId}.", campaign.Id, brand.Id);
                }
            }
            catch (Exception ex)
            {
                // One brand's bad timezone id, or a transient failure, must not stop every
                // other brand in the same pass from being checked.
                logger.LogError(ex, "Scheduled trigger failed for brand {BrandId}.", brand.Id);
            }
        }

        await jobs.EnqueueAsync(new CampaignTriggerScanPayload(), tenantId: null, runAt: now.Add(ScanInterval), ct: ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>True in the roughly one-hour window this handler's own firing interval covers.</summary>
    private static bool IsTriggerMoment(string timeZoneId, DateTimeOffset utcNow)
    {
        DateTimeOffset local;

        try
        {
            local = TimeZoneInfo.ConvertTime(utcNow, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId));
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }

        return local.DayOfWeek == DayOfWeek.Monday && local.Hour == TriggerHour;
    }

    private static DateOnly LocalDate(string timeZoneId, DateTimeOffset utcNow) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utcNow, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId)).Date);
}
