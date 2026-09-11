using System.Text.Json;
using ContentPilot.Application.Abstractions;
using ContentPilot.Application.Agents;
using ContentPilot.Application.Ai;
using ContentPilot.Application.Capabilities;
using ContentPilot.Application.Jobs;
using ContentPilot.Application.Prompts;
using ContentPilot.Domain.Content;
using ContentPilot.Infrastructure.Ai;
using ContentPilot.Infrastructure.Branding;
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
/// The campaign half of the loop, against real Postgres: planning fans out into items and
/// their own <c>WorkflowRun</c>s, and the campaign closes out once every item is terminal.
/// The item pipeline itself is <c>ContentItemWorkflowJobHandlerTests</c>' job — here, items
/// are promoted to their terminal status directly, so this suite tests exactly what is new:
/// the fan-out and the completion check.
/// </summary>
[Collection(ContentPilotCollection.Name)]
public sealed class CampaignWorkflowJobHandlerTests(ContentPilotFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 6, 0, 0, TimeSpan.Zero);
    private static int _week;

    [DockerFact]
    public async Task Planning_fans_out_exactly_the_quota_with_a_run_per_item()
    {
        var (campaign, scope) = await SetupAsync();
        await using var _scope = scope;

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var handler = BuildHandler(scope.ServiceProvider, PlanJson());

        await AdvanceAsync(handler, campaign.Id);

        var reloaded = await db.ContentCampaigns.AsNoTracking().SingleAsync(c => c.Id == campaign.Id);
        var items = await db.ContentItems.AsNoTracking().Where(i => i.CampaignId == campaign.Id).ToListAsync();
        var runs = await db.WorkflowRuns.AsNoTracking().Where(r => r.CampaignId == campaign.Id).ToListAsync();

        reloaded.Status.ShouldBe(CampaignStatus.ItemsRunning);
        reloaded.Theme.ShouldNotBeNullOrWhiteSpace();
        items.Count.ShouldBe(4); // 2 posts, 1 carousel, 1 reel — the golden tenant's quota.
        items.Count(i => i.Type == ContentItemType.StaticPost).ShouldBe(2);
        items.Count(i => i.Type == ContentItemType.Carousel).ShouldBe(1);
        items.Count(i => i.Type == ContentItemType.Reel).ShouldBe(1);

        // Every item got its own run — items are independent, so one bad reel cannot
        // block the rest, and that starts with each having separate bookkeeping.
        runs.Select(r => r.EntityId).ShouldBe(items.Select(i => i.Id), ignoreOrder: true);
    }

    [DockerFact]
    public async Task A_retry_after_planning_does_not_ask_the_strategist_twice()
    {
        var (campaign, scope) = await SetupAsync();
        await using var _scope = scope;

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var handler = BuildHandler(scope.ServiceProvider, PlanJson());

        await AdvanceAsync(handler, campaign.Id); // plans and fans out
        await AdvanceAsync(handler, campaign.Id); // a retry — items already exist

        (await db.ContentItems.CountAsync(i => i.CampaignId == campaign.Id)).ShouldBe(4);
    }

    [DockerFact]
    public async Task The_campaign_reaches_ready_once_every_item_is_approved()
    {
        var (campaign, scope) = await SetupAsync();
        await using var _scope = scope;

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var handler = BuildHandler(scope.ServiceProvider, PlanJson());

        await AdvanceAsync(handler, campaign.Id);

        foreach (var item in await db.ContentItems.Where(i => i.CampaignId == campaign.Id).ToListAsync())
        {
            item.MoveTo(ContentItemStatus.Approved);
        }

        await db.SaveChangesAsync();

        await AdvanceAsync(handler, campaign.Id);

        var reloaded = await db.ContentCampaigns.AsNoTracking().SingleAsync(c => c.Id == campaign.Id);
        reloaded.Status.ShouldBe(CampaignStatus.Ready);
        reloaded.CompletedAt.ShouldNotBeNull();
    }

    [DockerFact]
    public async Task A_partially_approved_campaign_still_closes_out_rather_than_hanging()
    {
        var (campaign, scope) = await SetupAsync();
        await using var _scope = scope;

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var handler = BuildHandler(scope.ServiceProvider, PlanJson());

        await AdvanceAsync(handler, campaign.Id);

        var items = await db.ContentItems.Where(i => i.CampaignId == campaign.Id).OrderBy(i => i.Ordinal).ToListAsync();

        for (var i = 0; i < items.Count; i++)
        {
            if (i == 0)
            {
                items[i].SendToHumanReview("carousels are not driven by this phase's item handler");
            }
            else
            {
                items[i].MoveTo(ContentItemStatus.Approved);
            }
        }

        await db.SaveChangesAsync();

        await AdvanceAsync(handler, campaign.Id);

        var reloaded = await db.ContentCampaigns.AsNoTracking().SingleAsync(c => c.Id == campaign.Id);

        // A week where most posts are good and one needs a human is a usable week, not a
        // broken one — that is the whole point of PartiallyReady existing as an outcome.
        reloaded.Status.ShouldBe(CampaignStatus.PartiallyReady);
    }

    [DockerFact]
    public async Task While_items_are_still_running_the_campaign_stays_open()
    {
        var (campaign, scope) = await SetupAsync();
        await using var _scope = scope;

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var handler = BuildHandler(scope.ServiceProvider, PlanJson());

        await AdvanceAsync(handler, campaign.Id);
        await AdvanceAsync(handler, campaign.Id); // items are all still Pending

        var reloaded = await db.ContentCampaigns.AsNoTracking().SingleAsync(c => c.Id == campaign.Id);
        reloaded.Status.ShouldBe(CampaignStatus.ItemsRunning);
        reloaded.IsTerminal.ShouldBeFalse();
    }

    [DockerFact]
    public async Task A_cancelled_campaign_is_left_alone_by_the_handler()
    {
        var (campaign, scope) = await SetupAsync();
        await using var _scope = scope;

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var handler = BuildHandler(scope.ServiceProvider, PlanJson());

        await AdvanceAsync(handler, campaign.Id); // plans and fans out

        var tracked = await db.ContentCampaigns.SingleAsync(c => c.Id == campaign.Id);
        tracked.Cancel(Now);
        await db.SaveChangesAsync();

        // A cancelled campaign is terminal, so the handler's very first check stops it —
        // no re-plan, no re-check, nothing overwrites the cancellation.
        await AdvanceAsync(handler, campaign.Id);

        var reloaded = await db.ContentCampaigns.AsNoTracking().SingleAsync(c => c.Id == campaign.Id);
        reloaded.Status.ShouldBe(CampaignStatus.Cancelled);
    }

    [DockerFact]
    public async Task A_strategist_that_cannot_produce_a_valid_plan_fails_the_campaign_with_a_reason()
    {
        // Wrong quota (one post instead of two) — the deterministic validator rejects it,
        // and the executor's two repair attempts both see the same malformed answer.
        var badPlan = """
            {"theme":"Only one post","items":[
              {"type":"StaticPost","topic":"A single lonely post","pillar":"problem-solution","objective":"Say one thing.","publish_day":"Tuesday","fact_keys":[]}
            ]}
            """;

        var (campaign, scope) = await SetupAsync();
        await using var _scope = scope;

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var handler = BuildHandler(scope.ServiceProvider, badPlan);

        await AdvanceAsync(handler, campaign.Id);

        var reloaded = await db.ContentCampaigns.AsNoTracking().SingleAsync(c => c.Id == campaign.Id);
        reloaded.Status.ShouldBe(CampaignStatus.Failed);
        reloaded.FailureReason.ShouldNotBeNullOrWhiteSpace();
        (await db.ContentItems.CountAsync(i => i.CampaignId == campaign.Id)).ShouldBe(0);
    }

    private static async Task AdvanceAsync(CampaignWorkflowJobHandler handler, Guid campaignId)
    {
        var context = new JobExecutionContext(Guid.CreateVersion7(), ((IJobHandler)handler).JobType, null, 1, Now, Now);
        var payloadJson = JsonSerializer.Serialize(new AdvanceCampaignWorkflowPayload(campaignId), JobSerialization.Options);

        await ((IJobHandler)handler).HandleAsync(context, payloadJson, CancellationToken.None);
    }

    private async Task<(ContentCampaign Campaign, AsyncServiceScope Scope)> SetupAsync()
    {
        var weekStart = new DateOnly(2031, 1, 6).AddDays(7 * Interlocked.Increment(ref _week));

        var scope = fixture.CreateScope();
        var seeder = scope.ServiceProvider.GetRequiredService<GoldenTenantSeeder>();
        var seeded = await seeder.SeedAsync();

        scope.ServiceProvider.GetRequiredService<IMutableTenantContext>().SetTenant(seeded.TenantId);

        var brandReader = scope.ServiceProvider.GetRequiredService<IBrandBrainReader>();
        var version = await brandReader.CaptureVersionAsync(seeded.BrandId);

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var campaign = new ContentCampaign(
            seeded.TenantId, seeded.BrandId, weekStart, CampaignTrigger.Manual, version.VersionId, 200_000_000);
        db.ContentCampaigns.Add(campaign);
        await db.SaveChangesAsync();

        return (campaign, scope);
    }

    private static CampaignWorkflowJobHandler BuildHandler(IServiceProvider services, string planJson)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var store = services.GetRequiredService<IObjectStore>();
        var tenantContext = services.GetRequiredService<ITenantContext>();
        var clock = services.GetRequiredService<IClock>();

        var model = new ScriptedModel(planJson);
        var agentExecutor = new AgentExecutor(
            model, PromptLibrary.LoadEmbedded(), services.GetRequiredService<PromptRegistry>(),
            db, store, tenantContext, clock, NullLogger<AgentExecutor>.Instance);

        return new CampaignWorkflowJobHandler(
            db,
            services.GetRequiredService<IJobQueue>(),
            clock,
            services.GetRequiredService<IBrandBrainReader>(),
            services.GetRequiredService<ContentMemoryReader>(),
            agentExecutor,
            new ContentStrategistAgent(),
            new ContentPilot.Infrastructure.Packaging.CampaignPackager(db, store, clock),
            NullLogger<CampaignWorkflowJobHandler>.Instance);
    }

    // The first topic is deliberately distinct from ContentItemWorkflowJobHandlerTests'
    // fixture topic ("Your chair sits empty...") — that test's item is really approved
    // through the job handler and now genuinely writes a ContentHistoryEntry (§14), and the
    // two suites share the golden tenant's brand, so an identical topic here would fail
    // this file's own PlanValidator novelty check as a same-week repeat of itself.
    private static string PlanJson() => """
        {"theme":"A week about never losing a booking","items":[
          {"type":"StaticPost","topic":"A no-show at 9pm still gets automatically rebooked","pillar":"problem-solution","objective":"Show automatic rebooking.","publish_day":"Tuesday","fact_keys":[]},
          {"type":"StaticPost","topic":"Clients who book at midnight still get confirmed","pillar":"feature-highlight","objective":"Highlight always-on booking.","publish_day":"Thursday","fact_keys":[]},
          {"type":"Carousel","topic":"Five ways salons lose bookings without noticing","pillar":"problem-solution","objective":"Walk through common failure points.","publish_day":"Saturday","fact_keys":[]},
          {"type":"Reel","topic":"A cancelled slot filling itself in real time","pillar":"social-proof","objective":"Show the rebooking flow in action.","publish_day":"Tuesday","fact_keys":[]}
        ]}
        """;

    /// <summary>Same scripted-model trick used across this suite: only the model is faked.</summary>
    private sealed class ScriptedModel(params string[] responses) : ILanguageModelClient
    {
        private int _call;

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
        {
            var json = responses[Math.Min(_call, responses.Length - 1)];
            _call++;

            return Task.FromResult(new LlmResponse
            {
                Json = json,
                ModelId = "scripted",
                Usage = new TokenUsage(600, 200, 0, 0),
                DurationMs = 300,
                CostMicroCents = 60_000,
            });
        }
    }
}
