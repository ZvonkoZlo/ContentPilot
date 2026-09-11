using ContentPilot.Application.Abstractions;
using ContentPilot.Application.Jobs;
using ContentPilot.Domain.Branding;
using ContentPilot.Domain.Content;
using ContentPilot.Domain.Observability;
using ContentPilot.Domain.Tenancy;
using ContentPilot.Infrastructure.Jobs;
using ContentPilot.Infrastructure.Persistence;
using ContentPilot.IntegrationTests.Infrastructure;
using ContentPilot.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace ContentPilot.IntegrationTests;

/// <summary>
/// §11's retention policy against real Postgres and MinIO. Neither <c>ContentAsset</c> nor
/// <c>AgentRun</c> can be touched by this job — both are append-only — so every assertion
/// here is about object-store existence, never a row disappearing or changing.
/// <para>
/// This handler genuinely iterates every active tenant, including ones other tests created
/// — the correct production behaviour, not a test leak — so assertions are scoped to
/// object keys this test itself created, never a count of "everything purged".
/// </para>
/// </summary>
[Collection(ContentPilotCollection.Name)]
public sealed class RetentionJobHandlerTests(ContentPilotFixture fixture)
{
    private static readonly DateTimeOffset Epoch = new(2036, 6, 1, 0, 0, 0, TimeSpan.Zero);

    [DockerFact]
    public async Task A_superseded_attempts_bytes_are_purged_but_the_winning_attempt_survives()
    {
        var (tenant, campaign, item, scope) = await SeedAsync(attemptRetentionDays: 1, payloadRetentionDays: 90);
        await using var _scope = scope;

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<IObjectStore>();

        var supersededKey = ObjectKey.ForTenant(tenant.Id, $"runs/{item.Id:N}/attempts/1/superseded.png");
        var winningKey = ObjectKey.ForTenant(tenant.Id, $"runs/{item.Id:N}/attempts/2/winning.png");
        await PutAsync(store, supersededKey, "attempt one");
        await PutAsync(store, winningKey, "attempt two");

        var superseded = new ContentAsset(
            tenant.Id, item.Id, 1, ContentAssetKind.Image, supersededKey, "image/png", "hash1", 11, Epoch);
        var winning = new ContentAsset(
            tenant.Id, item.Id, 2, ContentAssetKind.Image, winningKey, "image/png", "hash2", 11, Epoch);
        db.ContentAssets.AddRange(superseded, winning);
        await db.SaveChangesAsync();

        item.Approve(); // the highest attempt (2) is the winner — ResolveAssetAsync's own rule
        await db.SaveChangesAsync();

        var handler = BuildHandler(scope.ServiceProvider, now: Epoch.AddDays(2));
        await ((IJobHandler)handler).HandleAsync(Context(handler), "{}", CancellationToken.None);

        (await store.ExistsAsync(supersededKey)).ShouldBeFalse();
        (await store.ExistsAsync(winningKey)).ShouldBeTrue();
    }

    [DockerFact]
    public async Task An_item_still_in_flight_is_never_touched_even_if_its_asset_is_old()
    {
        var (tenant, _, item, scope) = await SeedAsync(attemptRetentionDays: 1, payloadRetentionDays: 90);
        await using var _scope = scope;

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<IObjectStore>();

        var key = ObjectKey.ForTenant(tenant.Id, $"runs/{item.Id:N}/attempts/1/in-flight.png");
        await PutAsync(store, key, "still rendering");

        db.ContentAssets.Add(new ContentAsset(tenant.Id, item.Id, 1, ContentAssetKind.Image, key, "image/png", "hash", 4, Epoch));
        await db.SaveChangesAsync();

        // item.Status stays Pending — never approved, never sent to review, never failed.

        var handler = BuildHandler(scope.ServiceProvider, now: Epoch.AddDays(2));
        await ((IJobHandler)handler).HandleAsync(Context(handler), "{}", CancellationToken.None);

        (await store.ExistsAsync(key)).ShouldBeTrue();
    }

    [DockerFact]
    public async Task A_stale_model_payload_is_purged_after_its_own_retention_window()
    {
        var (tenant, campaign, _, scope) = await SeedAsync(attemptRetentionDays: 90, payloadRetentionDays: 1);
        await using var _scope = scope;

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<IObjectStore>();

        var inputKey = ObjectKey.ForTenant(tenant.Id, "runs/x/input.txt");
        var outputKey = ObjectKey.ForTenant(tenant.Id, "runs/x/output.json");
        await PutAsync(store, inputKey, "the prompt");
        await PutAsync(store, outputKey, "the response");

        var run = new AgentRun(tenant.Id, campaign.Id, null, "strategist", "v1", Guid.CreateVersion7(), "claude-sonnet-5", 1, Epoch);
        run.AttachPayloads(inputKey.Value, outputKey.Value);
        run.Succeed(TokenUsageSnapshot.Empty, costMicroCents: 0, durationMs: 100, completedAt: Epoch);
        db.AgentRuns.Add(run);
        await db.SaveChangesAsync();

        var handler = BuildHandler(scope.ServiceProvider, now: Epoch.AddDays(2));
        await ((IJobHandler)handler).HandleAsync(Context(handler), "{}", CancellationToken.None);

        (await store.ExistsAsync(inputKey)).ShouldBeFalse();
        (await store.ExistsAsync(outputKey)).ShouldBeFalse();

        // The row itself is untouched — AgentRun is append-only, and its metrics survive
        // even after its archived payload is gone.
        var reloaded = await db.AgentRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id);
        reloaded.Outcome.ShouldBe(AgentRunOutcome.Succeeded);
    }

    [DockerFact]
    public async Task The_job_reschedules_itself_daily()
    {
        var (_, _, _, scope) = await SeedAsync(attemptRetentionDays: 90, payloadRetentionDays: 90);
        await using var _scope = scope;

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var before = await db.Jobs.CountAsync(j => j.Type == JobTypeName.For<EnforceRetentionPayload>());

        var handler = BuildHandler(scope.ServiceProvider, now: Epoch);
        await ((IJobHandler)handler).HandleAsync(Context(handler), "{}", CancellationToken.None);

        var after = await db.Jobs.CountAsync(j => j.Type == JobTypeName.For<EnforceRetentionPayload>());
        after.ShouldBe(before + 1);
    }

    private static async Task PutAsync(IObjectStore store, ObjectKey key, string content)
    {
        await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        await store.PutAsync(key, stream, "text/plain", default);
    }

    private static JobExecutionContext Context(IJobHandler handler) =>
        new(Guid.CreateVersion7(), handler.JobType, null, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private async Task<(Tenant Tenant, ContentCampaign Campaign, ContentItem Item, AsyncServiceScope Scope)> SeedAsync(
        int attemptRetentionDays, int payloadRetentionDays)
    {
        var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tenantContext = scope.ServiceProvider.GetRequiredService<IMutableTenantContext>();

        Tenant tenant;
        ContentCampaign campaign;
        ContentItem item;

        using (tenantContext.BeginCrossTenantScope())
        {
            var suffix = Guid.NewGuid().ToString("N")[..8];
            tenant = new Tenant($"Retention test {suffix}", $"retention-test-{suffix}", new TenantLimits
            {
                AttemptArtefactRetentionDays = attemptRetentionDays,
                ModelPayloadRetentionDays = payloadRetentionDays,
            });
            db.Tenants.Add(tenant);

            var brand = new Brand(tenant.Id, "Retention Brand", "UTC");
            db.Brands.Add(brand);
            await db.SaveChangesAsync();

            tenantContext.SetTenant(tenant.Id);

            campaign = new ContentCampaign(tenant.Id, brand.Id, new DateOnly(2036, 6, 1), CampaignTrigger.Manual, Guid.CreateVersion7(), 200_000_000);
            db.ContentCampaigns.Add(campaign);

            item = new ContentItem(tenant.Id, campaign.Id, ContentItemType.StaticPost, "Retention topic", "problem-solution", "n/a", DayOfWeek.Monday, 1);
            db.ContentItems.Add(item);

            await db.SaveChangesAsync();
        }

        tenantContext.SetTenant(tenant.Id);

        return (tenant, campaign, item, scope);
    }

    private static RetentionJobHandler BuildHandler(IServiceProvider services, DateTimeOffset now) => new(
        services.GetRequiredService<AppDbContext>(),
        services.GetRequiredService<IJobQueue>(),
        services.GetRequiredService<IObjectStore>(),
        new FixedClock(now),
        services.GetRequiredService<IMutableTenantContext>(),
        NullLogger<RetentionJobHandler>.Instance);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
