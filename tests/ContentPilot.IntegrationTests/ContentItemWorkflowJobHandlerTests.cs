using System.Text.Json;
using ContentPilot.Application.Abstractions;
using ContentPilot.Application.Agents;
using ContentPilot.Application.Ai;
using ContentPilot.Application.Capabilities;
using ContentPilot.Application.Jobs;
using ContentPilot.Application.Prompts;
using ContentPilot.Domain.Content;
using ContentPilot.Domain.Workflow;
using ContentPilot.Infrastructure.Ai;
using ContentPilot.Infrastructure.Branding;
using ContentPilot.Infrastructure.Jobs;
using ContentPilot.Infrastructure.Persistence;
using ContentPilot.IntegrationTests.Infrastructure;
using ContentPilot.Rendering.Contracts;
using ContentPilot.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace ContentPilot.IntegrationTests;

/// <summary>
/// The whole loop, end to end, against real Postgres and MinIO. Only two things are faked:
/// the model (a scripted response, the same trick <c>AgentExecutorTests</c> uses) and the
/// renderer (no Chromium needed to prove the orchestration, only the fidelity math the
/// renderer test suite already covers on its own). Everything else — the domain entities,
/// the state machine, the remediation router, the happy path — is the real code.
/// </summary>
[Collection(ContentPilotCollection.Name)]
public sealed class ContentItemWorkflowJobHandlerTests(ContentPilotFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 6, 0, 0, TimeSpan.Zero);
    private static int _week;

    private static readonly TemplateManifest Manifest = new()
    {
        TemplateId = "fake-template",
        Version = 1,
        Name = "Fake template",
        ContentTypes = [TemplateContentType.Static],
        AspectRatios = [AspectRatio.FourFive],
        TextSlots = [new TextSlot { Id = "headline", Role = "headline", MaxChars = 60, MaxLines = 3 }],
    };

    [DockerFact]
    public async Task A_clean_render_carries_a_static_post_all_the_way_to_approved()
    {
        var fixtureData = await SetupAsync(new FakeRendererClient(Manifest, alwaysOverflow: false));
        await using var _scope = fixtureData.Scope;

        await AdvanceAsync(fixtureData);

        var db = fixtureData.Scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var reloaded = await db.ContentItems.AsNoTracking().SingleAsync(i => i.Id == fixtureData.Item.Id);
        var run = await db.WorkflowRuns.AsNoTracking().SingleAsync(r => r.EntityId == fixtureData.Item.Id);

        reloaded.Status.ShouldBe(ContentItemStatus.Approved);
        run.State.ShouldBe(WorkflowRunState.Completed);

        (await db.CreativeSpecs.CountAsync(s => s.ContentItemId == fixtureData.Item.Id)).ShouldBe(1);
        (await db.ContentAssets.CountAsync(a => a.ContentItemId == fixtureData.Item.Id)).ShouldBe(1);

        var review = await db.QualityReviews.AsNoTracking().SingleAsync(q => q.ContentItemId == fixtureData.Item.Id);
        review.Findings.ShouldBeEmpty();
    }

    [DockerFact]
    public async Task A_render_that_always_overflows_converges_to_human_review_rather_than_looping_forever()
    {
        var fixtureData = await SetupAsync(new FakeRendererClient(Manifest, alwaysOverflow: true));
        await using var _scope = fixtureData.Scope;

        var db = fixtureData.Scope.ServiceProvider.GetRequiredService<AppDbContext>();
        ContentItemStatus status;
        var iterations = 0;

        // Every attempt fails the same way. The escalation ladder — restart, restart,
        // SafeMode fallback, human review — has to converge in a bounded number of job
        // invocations, not loop forever.
        do
        {
            await AdvanceAsync(fixtureData);
            status = (await db.ContentItems.AsNoTracking().SingleAsync(i => i.Id == fixtureData.Item.Id)).Status;
            iterations++;
        }
        while (status != ContentItemStatus.NeedsHumanReview && iterations < 6);

        var reloaded = await db.ContentItems.AsNoTracking().SingleAsync(i => i.Id == fixtureData.Item.Id);
        var run = await db.WorkflowRuns.AsNoTracking().SingleAsync(r => r.EntityId == fixtureData.Item.Id);

        reloaded.Status.ShouldBe(ContentItemStatus.NeedsHumanReview);
        run.State.ShouldBe(WorkflowRunState.NeedsHumanReview);

        // Every attempt left a CreativeSpec and a ContentRevision behind — the best attempt
        // so far is promotable precisely because the earlier ones survive.
        (await db.CreativeSpecs.CountAsync(s => s.ContentItemId == fixtureData.Item.Id)).ShouldBeGreaterThan(1);
        (await db.ContentRevisions.CountAsync(r => r.ContentItemId == fixtureData.Item.Id)).ShouldBeGreaterThan(0);
    }

    private static async Task AdvanceAsync(TestFixtureData data)
    {
        var context = new JobExecutionContext(
            Guid.CreateVersion7(), data.Handler.JobType, data.TenantId, 1, Now, Now);

        var payloadJson = JsonSerializer.Serialize(new AdvanceContentItemWorkflowPayload(data.Item.Id), JobSerialization.Options);

        await ((IJobHandler)data.Handler).HandleAsync(context, payloadJson, CancellationToken.None);
    }

    private async Task<TestFixtureData> SetupAsync(FakeRendererClient renderer)
    {
        // A base date far from every other integration test's campaign weeks — the golden
        // brand is shared process-wide, and (BrandId, WeekStart) is unique.
        var weekStart = new DateOnly(2030, 1, 7).AddDays(7 * Interlocked.Increment(ref _week));

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

        var item = new ContentItem(
            seeded.TenantId, campaign.Id, ContentItemType.StaticPost,
            "Your chair sits empty when someone cancels at 9pm", "problem-solution",
            "Show that booking keeps filling the slot automatically.", DayOfWeek.Monday, 1);
        db.ContentItems.Add(item);
        await db.SaveChangesAsync();

        var run = new WorkflowRun(seeded.TenantId, campaign.Id, WorkflowScope.Item, item.Id, Now);
        db.WorkflowRuns.Add(run);
        await db.SaveChangesAsync();

        var store = scope.ServiceProvider.GetRequiredService<IObjectStore>();
        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        var model = new ScriptedModel(CopyResponse());
        var agentExecutor = new AgentExecutor(
            model, PromptLibrary.LoadEmbedded(), scope.ServiceProvider.GetRequiredService<PromptRegistry>(),
            db, store, tenantContext, clock, NullLogger<AgentExecutor>.Instance);

        var handler = new ContentItemWorkflowJobHandler(
            db,
            scope.ServiceProvider.GetRequiredService<IJobQueue>(),
            clock,
            brandReader,
            renderer,
            scope.ServiceProvider.GetRequiredService<IAssetContentResolver>(),
            agentExecutor,
            new CopywriterAgent(),
            NullLogger<ContentItemWorkflowJobHandler>.Instance);

        return new TestFixtureData(item, seeded.TenantId, scope, handler);
    }

    private static string CopyResponse() => """
        {"slots":[{"id":"headline","text":"Fill the empty slots in your week"}],"fact_citations":[]}
        """;

    private sealed record TestFixtureData(
        ContentItem Item, Guid TenantId, AsyncServiceScope Scope, ContentItemWorkflowJobHandler Handler);

    /// <summary>Same scripted-model trick <c>AgentExecutorTests</c> uses: only the model is faked.</summary>
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
                Usage = new TokenUsage(400, 120, 0, 0),
                DurationMs = 300,
                CostMicroCents = 40_000,
            });
        }
    }

    /// <summary>
    /// Stands in for the Renderer service. <paramref name="alwaysOverflow"/> lets one test
    /// prove the escalation ladder converges: every attempt reports the same clipped
    /// headline, so the router restarts, restarts, falls back to SafeMode, and finally gives
    /// up — the same policy already proven in isolation, now driven by a real job handler.
    /// </summary>
    private sealed class FakeRendererClient(TemplateManifest manifest, bool alwaysOverflow) : IRendererClient
    {
        public Task<IReadOnlyList<TemplateManifest>> GetManifestsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TemplateManifest>>([manifest]);

        public Task<TemplateManifest> GetManifestAsync(string templateId, CancellationToken ct = default) =>
            string.Equals(templateId, manifest.TemplateId, StringComparison.Ordinal)
                ? Task.FromResult(manifest)
                : throw new RendererTemplateNotFoundException(templateId);

        public Task<RenderImageResponse> RenderAsync(RenderImageRequest request, CancellationToken ct = default)
        {
            var (width, height) = request.AspectRatio.Dimensions();

            var slot = new SlotMeasurement
            {
                SlotId = "headline",
                Box = new BoundingBox { X = 60, Y = 80, Width = width - 120, Height = 200 },
                Overflows = alwaysOverflow,
                LineCount = 2,
                FontSizePx = 64,
                ContrastRatio = 9.4,
                BackdropLuminance = 0.08,
                Occlusion = 0,
                BreaksSafeArea = false,
            };

            return Task.FromResult(new RenderImageResponse
            {
                TemplateId = request.TemplateId,
                TemplateVersion = request.TemplateVersion ?? manifest.Version,
                Image = ImagePayload.FromBytes(new byte[24_000], "image/png"),
                Report = new RenderReport
                {
                    Width = width,
                    Height = height,
                    DeviceScaleFactor = 2,
                    Slots = [slot],
                    FontsLoaded = [request.Brand.HeadingFont, request.Brand.BodyFont],
                    RenderDurationMs = 400,
                },
            });
        }

        public Task<CompareResult> CompareAsync(CompareRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException("The fake template declares no immutable asset slots.");
    }
}
