using ContentPilot.Application.Abstractions;
using ContentPilot.Application.Capabilities;
using ContentPilot.Application.Jobs;
using ContentPilot.Domain.Branding;
using ContentPilot.Domain.Content;
using ContentPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ContentPilot.Infrastructure.Campaigns;

/// <summary>
/// §21's single implementation behind all three triggers — manual, the weekly schedule, and
/// the daily reconciler that catches a missed one. Whichever caller reaches this, starting a
/// campaign means exactly one thing: check the week isn't already claimed, freeze the brand,
/// create the row, enqueue the job. A second code path for any one trigger is how a manual
/// "generate now" and a cron firing start disagreeing about what starting a campaign means.
/// <para>
/// Callers are responsible for the ambient tenant already being <paramref name="brand"/>'s
/// own — this does not set it, since a caller iterating many brands needs to control that
/// scoping itself — and for calling <see cref="IUnitOfWork.SaveChangesAsync"/> afterwards,
/// matching the "enqueue does not save" convention every job-issuing call in this system
/// follows.
/// </para>
/// </summary>
public sealed class CampaignStarter(AppDbContext db, IBrandBrainReader brandReader, IJobQueue jobs)
{
    /// <summary>Null means a campaign for this brand and week already exists — not an error, just nothing to do.</summary>
    public async Task<ContentCampaign?> StartAsync(
        Brand brand, DateOnly weekStart, CampaignTrigger trigger, CancellationToken ct = default)
    {
        // The unique index on (BrandId, WeekStart) is the real guarantee; this check only
        // avoids freezing a brand version and writing a row that would be rejected anyway.
        var exists = await db.ContentCampaigns.AnyAsync(c => c.BrandId == brand.Id && c.WeekStart == weekStart, ct);

        if (exists)
        {
            return null;
        }

        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == brand.TenantId, ct)
            ?? throw new InvalidOperationException($"Brand {brand.Id} references tenant '{brand.TenantId}', which does not exist.");

        // Frozen at trigger time, not read live later — every agent this campaign runs
        // works from this exact snapshot, so an edit to the live profile mid-week cannot
        // change what a campaign already in flight is working from.
        var version = await brandReader.CaptureVersionAsync(brand.Id, ct);

        var campaign = new ContentCampaign(
            brand.TenantId, brand.Id, weekStart, trigger, version.VersionId, tenant.Limits.MaxCostPerCampaignMicroCents);

        db.ContentCampaigns.Add(campaign);

        await jobs.EnqueueAsync(
            new AdvanceCampaignWorkflowPayload(campaign.Id), brand.TenantId,
            idempotencyKey: $"campaign-plan:{campaign.Id:N}", ct: ct);

        return campaign;
    }
}
