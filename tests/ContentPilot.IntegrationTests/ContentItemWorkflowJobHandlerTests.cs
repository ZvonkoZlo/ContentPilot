using System.Text.Json;
using ContentPilot.Application.Abstractions;
using ContentPilot.Application.Agents;
using ContentPilot.Application.Ai;
using ContentPilot.Application.Capabilities;
using ContentPilot.Application.Jobs;
using ContentPilot.Application.Prompts;
using ContentPilot.Domain.Content;
using ContentPilot.Domain.Quality;
using ContentPilot.Domain.Workflow;
using ContentPilot.Infrastructure.Ai;
using ContentPilot.Infrastructure.Branding;
using ContentPilot.Infrastructure.Jobs;
using ContentPilot.Infrastructure.Persistence;
using ContentPilot.IntegrationTests.Infrastructure;
using ContentPilot.Rendering.Contracts;
using ContentPilot.TestSupport;
using ImageMagick;
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

        // Gate 1 passed clean, so gates 2 and 3 ran too — three reviews, one per gate,
        // every one of them clean.
        var reviews = await db.QualityReviews.AsNoTracking().Where(q => q.ContentItemId == fixtureData.Item.Id).ToListAsync();
        reviews.Select(r => r.Gate).ShouldBe([QaGate.Deterministic, QaGate.Visual, QaGate.Marketing], ignoreOrder: true);
        reviews.ShouldAllBe(r => r.Findings.Count == 0,
            string.Join(" | ", reviews.Select(r => $"{r.Gate}: {string.Join(", ", r.Findings.Select(f => $"{f.Code}({f.Detail})"))}")));
    }

    [DockerFact]
    public async Task An_item_ceiling_too_small_for_even_one_writing_call_sends_it_to_human_review_before_spending()
    {
        // A ceiling of one micro-cent cannot possibly cover the copywriter's estimated
        // worst-case cost, so the reserve-then-commit check in §24 has to refuse the call
        // before the model is ever reached — the whole point of checking first.
        var tightLimits = Domain.Tenancy.TenantLimits.Default with { MaxCostPerItemMicroCents = 1 };
        var fixtureData = await SetupAsync(new FakeRendererClient(Manifest, alwaysOverflow: false), tightLimits);
        await using var _scope = fixtureData.Scope;

        await AdvanceAsync(fixtureData);

        var db = fixtureData.Scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var reloaded = await db.ContentItems.AsNoTracking().SingleAsync(i => i.Id == fixtureData.Item.Id);
        var run = await db.WorkflowRuns.AsNoTracking().SingleAsync(r => r.EntityId == fixtureData.Item.Id);

        reloaded.Status.ShouldBe(ContentItemStatus.NeedsHumanReview);
        run.State.ShouldBe(WorkflowRunState.NeedsHumanReview);

        // No CostEntry exists because the model was never actually called, and no
        // reservation was left dangling — the check happens before either would appear.
        (await db.CostEntries.CountAsync(e => e.ContentItemId == fixtureData.Item.Id)).ShouldBe(0);
        (await db.BudgetReservations.CountAsync(r => r.ContentItemId == fixtureData.Item.Id && r.ReleasedAt == null)).ShouldBe(0);

        // The golden tenant is shared, idempotent, and reused by every other test in this
        // suite (which all run in the same xUnit collection, sequentially) — leaving its
        // limits tightened would silently break every test that runs after this one.
        var tenant = await db.Tenants.SingleAsync(t => t.Id == fixtureData.TenantId);
        tenant.UpdateLimits(Domain.Tenancy.TenantLimits.Default);
        await db.SaveChangesAsync();
    }

    [DockerFact]
    public async Task A_crash_after_writing_succeeds_resumes_without_re_billing_the_model()
    {
        var fixtureData = await SetupAsync(new FakeRendererClient(Manifest, alwaysOverflow: false));
        await using var _scope = fixtureData.Scope;

        var db = fixtureData.Scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var run = await db.WorkflowRuns.SingleAsync(r => r.EntityId == fixtureData.Item.Id);
        var item = await db.ContentItems.SingleAsync(i => i.Id == fixtureData.Item.Id);

        // Exactly the state a real crash mid-render would leave behind: Directing and
        // Writing each committed their own WorkflowStep row independently and the item
        // moved to SpecAssembly, but nothing after that ever ran.
        var directingStep = new WorkflowStep(item.TenantId, run.Id, nameof(ContentItemStatus.Directing), 1, Now);
        directingStep.Succeed(Now, resultJson:
            """{"templateId":"fake-template","templateVersion":1,"aspectRatio":"FourFive","assetAssignments":{}}""");
        db.WorkflowSteps.Add(directingStep);

        var writingStep = new WorkflowStep(item.TenantId, run.Id, nameof(ContentItemStatus.Writing), 1, Now);
        writingStep.Succeed(Now, resultJson: CopyResponse());
        db.WorkflowSteps.Add(writingStep);

        item.MoveTo(ContentItemStatus.Writing);
        item.MoveTo(ContentItemStatus.SpecAssembly);
        await db.SaveChangesAsync();

        await AdvanceAsync(fixtureData);

        var reloaded = await db.ContentItems.AsNoTracking().SingleAsync(i => i.Id == item.Id);
        reloaded.Status.ShouldBe(ContentItemStatus.Approved);

        // Writing's own model call is never repeated — the two AgentRuns that do exist are
        // VisualQA and MarketingQA, legitimately new work for this attempt that nothing
        // before this resumed pass had ever run. Zero CostEntry rows for the copywriter
        // specifically is the real assertion that Writing was skipped rather than redone.
        var runs = await db.AgentRuns.AsNoTracking().Where(r => r.ContentItemId == item.Id).ToListAsync();
        runs.Select(r => r.AgentName).ShouldBe(["visual-qa", "marketing-qa"], ignoreOrder: true);

        var costEntries = await db.CostEntries.AsNoTracking().Where(e => e.ContentItemId == item.Id).ToListAsync();
        costEntries.ShouldAllBe(e => e.AgentRunId != null && runs.Select(r => r.Id).Contains(e.AgentRunId.Value));
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

        // Work is never thrown away: even though every attempt failed the same way, one of
        // them is promoted rather than the item ending with nothing to show a reviewer.
        reloaded.BestAssetId.ShouldNotBeNull();
        (await db.ContentAssets.AnyAsync(a => a.Id == reloaded.BestAssetId)).ShouldBeTrue();
    }

    private static async Task AdvanceAsync(TestFixtureData data)
    {
        var context = new JobExecutionContext(
            Guid.CreateVersion7(), data.Handler.JobType, data.TenantId, 1, Now, Now);

        var payloadJson = JsonSerializer.Serialize(new AdvanceContentItemWorkflowPayload(data.Item.Id), JobSerialization.Options);

        await ((IJobHandler)data.Handler).HandleAsync(context, payloadJson, CancellationToken.None);
    }

    private async Task<TestFixtureData> SetupAsync(FakeRendererClient renderer, Domain.Tenancy.TenantLimits? limits = null)
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

        if (limits is not null)
        {
            var tenant = await db.Tenants.SingleAsync(t => t.Id == seeded.TenantId);
            tenant.UpdateLimits(limits);
            await db.SaveChangesAsync();
        }

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

        var model = new ScriptedModel();
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
            new VisualQaAgent(),
            new MarketingQaAgent(),
            scope.ServiceProvider.GetRequiredService<ContentMemoryReader>(),
            new FakeModelProfileRegistry(),
            PromptLibrary.LoadEmbedded(),
            NullLogger<ContentItemWorkflowJobHandler>.Instance);

        return new TestFixtureData(item, seeded.TenantId, scope, handler);
    }

    private static string CopyResponse() => """
        {"slots":[{"id":"headline","text":"Fill the empty slots in your week"}],"fact_citations":[]}
        """;

    private sealed record TestFixtureData(
        ContentItem Item, Guid TenantId, AsyncServiceScope Scope, ContentItemWorkflowJobHandler Handler);

    /// <summary>
    /// A minimal profile registry so the budget estimate has real prices to work with,
    /// without pulling the fixture's shared DI container (and its own Ai:Profiles gap)
    /// into a test that otherwise fakes the model directly.
    /// </summary>
    private sealed class FakeModelProfileRegistry : IModelProfileRegistry
    {
        private readonly ModelProfile _copywriter = new()
        {
            Name = "copywriter",
            Provider = ModelProvider.Anthropic,
            ModelId = "test-model",
            MaxOutputTokens = 4000,
            InputPricePerMillion = 500_000_000,
            OutputPricePerMillion = 2_500_000_000,
        };

        public ModelProfile Get(string name) => _copywriter;

        public IReadOnlyCollection<ModelProfile> All => [_copywriter];
    }

    /// <summary>
    /// Same scripted-model trick <c>AgentExecutorTests</c> uses: only the model is faked.
    /// Keyed by profile name, since a single attempt now calls three different agents
    /// (Writing, and — once gate 1 passes — VisualQA and MarketingQA in parallel).
    /// </summary>
    private sealed class ScriptedModel(
        string? copywriterResponse = null, string? visualQaResponse = null, string? marketingQaResponse = null)
        : ILanguageModelClient
    {
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
        {
            var json = request.Profile switch
            {
                "copywriter" => copywriterResponse ?? CopyResponse(),
                "visual-qa" => visualQaResponse ?? """{"findings":[]}""",
                "marketing-qa" => marketingQaResponse ?? """{"findings":[]}""",
                _ => throw new InvalidOperationException($"ScriptedModel was not told what to answer for profile '{request.Profile}'."),
            };

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
                // A real, decodable PNG — VisualQA's thumbnail step actually opens these
                // bytes now, so zero-filled padding (which satisfied only the deterministic
                // gate's byte-count check) is no longer enough.
                Image = FakePng(width, height),
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

        /// <summary>
        /// Noise rather than a flat fill, so compression cannot shrink it under the
        /// deterministic gate's minimum-plausible-bytes floor the way a solid colour would —
        /// JPEG rather than PNG, so that same noise does not blow past the maximum-plausible
        /// ceiling instead, the way lossless compression of pure noise does.
        /// </summary>
        private static ImagePayload FakePng(int width, int height)
        {
            using var image = new MagickImage(MagickColors.White, (uint)width, (uint)height);
            image.AddNoise(NoiseType.Random);
            image.Format = MagickFormat.Jpeg;
            image.Quality = 85;

            return ImagePayload.FromBytes(image.ToByteArray(), "image/jpeg");
        }
    }
}
