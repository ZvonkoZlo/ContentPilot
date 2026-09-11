using ContentPilot.Application.Abstractions;
using ContentPilot.Application.Jobs;
using ContentPilot.Domain.Content;
using ContentPilot.Domain.Tenancy;
using ContentPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ContentPilot.Infrastructure.Jobs;

/// <summary>
/// §11's retention table, enforced nightly, per tenant policy: "failed attempt artefacts 30
/// days, LLM payload archive 90 days, approved campaign assets indefinite until deletion."
/// <para>
/// Both <see cref="ContentAsset"/> and <c>AgentRun</c> are <c>IAppendOnly</c> — the DbContext
/// refuses to modify or delete either row outright, by design, because they are the audit
/// trail. Retention therefore never touches a row: it deletes only the object-storage bytes
/// the row references (<see cref="ContentAsset.StorageKey"/>, an <c>AgentRun</c>'s
/// <c>InputRef</c>/<c>OutputRef</c>), leaving the row's metrics and hashes in place forever.
/// An object delete is idempotent (S3 returns success for a key that is already gone), so
/// re-running this job every night against the same already-purged rows costs one no-op
/// request each, not an error.
/// </para>
/// <para>
/// <b>Known tradeoff, not an oversight.</b> Because no row can be marked "already purged"
/// without a write the append-only guard forbids, every stale row is re-queried and
/// re-requested for deletion on every future run, forever — the per-run object-store cost
/// stays flat (no-ops are cheap) but the Postgres query work grows with total historical
/// volume. Fine at MVP scale (§28: one VM, one tenant, eight items a week); the honest fix
/// if that ever matters is a small non-append-only sidecar table recording which artefact
/// IDs were purged, not a change to <see cref="ContentAsset"/> or <c>AgentRun</c> themselves.
/// </para>
/// </summary>
public sealed class RetentionJobHandler(
    AppDbContext db,
    IJobQueue jobs,
    IObjectStore store,
    IClock clock,
    IMutableTenantContext tenantContext,
    ILogger<RetentionJobHandler> logger)
    : JobHandler<EnforceRetentionPayload>
{
    private static readonly TimeSpan RunInterval = TimeSpan.FromDays(1);

    protected override async Task HandleAsync(JobExecutionContext context, EnforceRetentionPayload payload, CancellationToken ct)
    {
        List<Tenant> tenants;

        using (tenantContext.BeginCrossTenantScope())
        {
            tenants = await db.Tenants.AsNoTracking().Where(t => t.IsActive).ToListAsync(ct);
        }

        var artefactsPurged = 0;
        var payloadsPurged = 0;

        foreach (var tenant in tenants)
        {
            tenantContext.SetTenant(tenant.Id);

            try
            {
                artefactsPurged += await PurgeStaleAttemptArtefactsAsync(tenant, ct);
                payloadsPurged += await PurgeStaleModelPayloadsAsync(tenant, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Retention pass failed for tenant {TenantId}.", tenant.Id);
            }
        }

        if (artefactsPurged > 0 || payloadsPurged > 0)
        {
            logger.LogInformation(
                "Retention purged {Artefacts} attempt artefact(s) and {Payloads} model payload(s).",
                artefactsPurged, payloadsPurged);
        }

        await jobs.EnqueueAsync(new EnforceRetentionPayload(), tenantId: null, runAt: clock.UtcNow.Add(RunInterval), ct: ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Purges every non-winning attempt's rendered bytes once its item has gone terminal and
    /// the retention window has passed. The winning attempt — <c>BestAssetId</c> when set,
    /// otherwise the highest attempt number, the same rule <c>CampaignPackager</c> uses — is
    /// never purged regardless of age: it is what "approved campaign assets indefinite until
    /// deletion" protects.
    /// </summary>
    private async Task<int> PurgeStaleAttemptArtefactsAsync(Tenant tenant, CancellationToken ct)
    {
        var cutoff = clock.UtcNow.AddDays(-tenant.Limits.AttemptArtefactRetentionDays);

        var staleItemIds = await db.ContentAssets.AsNoTracking()
            .Where(a => a.CreatedAt < cutoff)
            .Select(a => a.ContentItemId)
            .Distinct()
            .ToListAsync(ct);

        var purged = 0;

        foreach (var itemId in staleItemIds)
        {
            var item = await db.ContentItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId, ct);

            if (item is null || !item.IsTerminal)
            {
                // Still in flight, or the item's row is gone — nothing here has "failed"
                // yet, so nothing about it is eligible for purge.
                continue;
            }

            var assets = await db.ContentAssets.AsNoTracking()
                .Where(a => a.ContentItemId == itemId)
                .ToListAsync(ct);

            var keptAssetId = item.BestAssetId
                ?? assets.OrderByDescending(a => a.Attempt).Select(a => (Guid?)a.Id).FirstOrDefault();

            foreach (var asset in assets)
            {
                if (asset.Id == keptAssetId || asset.CreatedAt >= cutoff)
                {
                    continue;
                }

                await store.DeleteAsync(ObjectKey.FromExisting(asset.StorageKey), ct);
                purged++;
            }
        }

        return purged;
    }

    private async Task<int> PurgeStaleModelPayloadsAsync(Tenant tenant, CancellationToken ct)
    {
        var cutoff = clock.UtcNow.AddDays(-tenant.Limits.ModelPayloadRetentionDays);

        var stale = await db.AgentRuns.AsNoTracking()
            .Where(r => r.StartedAt < cutoff && (r.InputRef != null || r.OutputRef != null))
            .Select(r => new { r.InputRef, r.OutputRef })
            .ToListAsync(ct);

        var purged = 0;

        foreach (var run in stale)
        {
            if (run.InputRef is { } inputRef)
            {
                await store.DeleteAsync(ObjectKey.FromExisting(inputRef), ct);
                purged++;
            }

            if (run.OutputRef is { } outputRef)
            {
                await store.DeleteAsync(ObjectKey.FromExisting(outputRef), ct);
                purged++;
            }
        }

        return purged;
    }
}
