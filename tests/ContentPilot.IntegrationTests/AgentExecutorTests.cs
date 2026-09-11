using System.Text.Json;
using ContentPilot.Application.Abstractions;
using ContentPilot.Application.Agents;
using ContentPilot.Application.Ai;
using ContentPilot.Application.Prompts;
using ContentPilot.Domain.Branding;
using ContentPilot.Domain.Content;
using ContentPilot.Domain.Observability;
using ContentPilot.Infrastructure.Ai;
using ContentPilot.Infrastructure.Branding;
using ContentPilot.Infrastructure.Persistence;
using ContentPilot.IntegrationTests.Infrastructure;
using ContentPilot.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace ContentPilot.IntegrationTests;

/// <summary>
/// The executor against real Postgres and real MinIO, with a scripted model.
/// <para>
/// The model is the one part that is faked, because the point here is everything around it:
/// that a run is recorded, that payloads are archived, that a rejected plan is repaired with
/// the exact problems, and that giving up leaves a trail rather than a silence.
/// </para>
/// </summary>
[Collection(ContentPilotCollection.Name)]
public sealed class AgentExecutorTests(ContentPilotFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 6, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// One campaign per brand per week is a real constraint, and these tests share the
    /// seeded brand. Each takes its own week rather than the schema being loosened to suit
    /// the suite.
    /// </summary>
    private static int _week;

    [DockerFact]
    public async Task A_valid_plan_is_recorded_with_its_prompt_and_payloads()
    {
        var (scope, campaignId, input) = await SetupAsync();
        await using var _ = scope;

        var model = new ScriptedModel(Plan(4));
        var executor = Executor(scope, model);

        var result = await executor.RunAsync(new ContentStrategistAgent(), input,
            new AgentContext { CampaignId = campaignId });

        result.Value.Items.Count.ShouldBe(4);
        result.Attempts.ShouldBe(1);
        result.PromptReference.ShouldBe("content-strategist@v1");

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var run = await db.AgentRuns.AsNoTracking().SingleAsync(r => r.CampaignId == campaignId);

        run.Outcome.ShouldBe(AgentRunOutcome.Succeeded);
        run.AgentName.ShouldBe("content-strategist");
        run.InputTokens.ShouldBeGreaterThan(0);

        // Payloads live in object storage, not Postgres: large, read rarely, and only when
        // something has gone wrong.
        run.InputRef.ShouldNotBeNull();
        run.OutputRef.ShouldNotBeNull();

        var store = scope.ServiceProvider.GetRequiredService<IObjectStore>();
        await using var archived = await store.GetAsync(ObjectKey.FromExisting(run.InputRef!));
        var text = await new StreamReader(archived).ReadToEndAsync();

        text.ShouldContain("Appointso");
        text.ShouldContain("Product facts");
    }

    [DockerFact]
    public async Task A_rejected_plan_is_repaired_with_the_exact_problems()
    {
        var (scope, campaignId, input) = await SetupAsync();
        await using var _ = scope;

        // First answer is short by one item; second is correct.
        var model = new ScriptedModel(Plan(3), Plan(4));
        var executor = Executor(scope, model);

        var result = await executor.RunAsync(new ContentStrategistAgent(), input,
            new AgentContext { CampaignId = campaignId });

        result.Attempts.ShouldBe(2);
        result.RepairedProblems.ShouldContain(p => p.Contains("Reel item(s), got 0"));

        // The repair carries the mechanical failures verbatim rather than a vague nudge —
        // and appends them to the original request, so the cached prefix still applies.
        model.LastUserPrompt.ShouldContain("Your previous answer was rejected");
        model.LastUserPrompt.ShouldContain("Reel item(s), got 0");
        model.LastUserPrompt.ShouldContain("Appointso");

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var runs = await db.AgentRuns.AsNoTracking()
            .Where(r => r.CampaignId == campaignId).OrderBy(r => r.Attempt).ToListAsync();

        runs.Count.ShouldBe(2);
        runs[0].Outcome.ShouldBe(AgentRunOutcome.ValidationFailed);
        runs[0].ValidationFailure.ShouldNotBeNull().ShouldContain("Reel");
        runs[1].Outcome.ShouldBe(AgentRunOutcome.Succeeded);
    }

    [DockerFact]
    public async Task Giving_up_leaves_a_trail_rather_than_a_silence()
    {
        var (scope, campaignId, input) = await SetupAsync();
        await using var _ = scope;

        var model = new ScriptedModel(Plan(3), Plan(3));
        var executor = Executor(scope, model);

        var ex = await Should.ThrowAsync<AgentValidationException>(
            () => executor.RunAsync(new ContentStrategistAgent(), input,
                new AgentContext { CampaignId = campaignId }));

        ex.Problems.ShouldNotBeEmpty();

        // Two attempts, not more: a third identical request rarely helps and always bills.
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var runs = await db.AgentRuns.AsNoTracking().Where(r => r.CampaignId == campaignId).ToListAsync();

        runs.Count.ShouldBe(AgentExecutor.MaxRepairAttempts);
        runs.ShouldAllBe(r => r.Outcome == AgentRunOutcome.ValidationFailed);
    }

    [DockerFact]
    public async Task A_refusal_is_recorded_as_a_declined_run_rather_than_a_crash()
    {
        var (scope, campaignId, input) = await SetupAsync();
        await using var _ = scope;

        var executor = Executor(scope, new ScriptedModel { RefusalCategory = "policy" });

        await Should.ThrowAsync<AgentValidationException>(
            () => executor.RunAsync(new ContentStrategistAgent(), input,
                new AgentContext { CampaignId = campaignId }));

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var run = await db.AgentRuns.AsNoTracking().SingleAsync(r => r.CampaignId == campaignId);

        run.Outcome.ShouldBe(AgentRunOutcome.ValidationFailed);
        run.ValidationFailure.ShouldNotBeNull().ShouldContain("declined");
    }

    [DockerFact]
    public async Task Editing_a_prompt_without_bumping_its_version_is_refused()
    {
        var (scope, _, _) = await SetupAsync();
        await using var _scope = scope;

        var registry = scope.ServiceProvider.GetRequiredService<PromptRegistry>();
        var real = PromptLibrary.LoadEmbedded().Get("content-strategist");

        await registry.ResolveAsync(real);

        // Same id and version, different text. Accepting it would silently rewrite the
        // meaning of every run already recorded against that version.
        var edited = real with { System = real.System + "\n\nAn instruction added later." };

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => new PromptRegistry(
                    scope.ServiceProvider.GetRequiredService<AppDbContext>(),
                    scope.ServiceProvider.GetRequiredService<IMutableTenantContext>(),
                    scope.ServiceProvider.GetRequiredService<IClock>(),
                    NullLogger<PromptRegistry>.Instance)
                .ResolveAsync(edited));

        ex.Message.ShouldContain("without bumping its version");
    }

    private static AgentExecutor Executor(AsyncServiceScope scope, ILanguageModelClient model) =>
        new(model,
            PromptLibrary.LoadEmbedded(),
            scope.ServiceProvider.GetRequiredService<PromptRegistry>(),
            scope.ServiceProvider.GetRequiredService<AppDbContext>(),
            scope.ServiceProvider.GetRequiredService<IObjectStore>(),
            scope.ServiceProvider.GetRequiredService<ITenantContext>(),
            scope.ServiceProvider.GetRequiredService<IClock>(),
            NullLogger<AgentExecutor>.Instance);

    private async Task<(AsyncServiceScope Scope, Guid CampaignId, StrategistInput Input)> SetupAsync()
    {
        var weekStart = new DateOnly(2026, 9, 14).AddDays(7 * Interlocked.Increment(ref _week));

        var scope = fixture.CreateScope();
        var seeder = scope.ServiceProvider.GetRequiredService<GoldenTenantSeeder>();
        var seeded = await seeder.SeedAsync();

        scope.ServiceProvider.GetRequiredService<IMutableTenantContext>().SetTenant(seeded.TenantId);

        var reader = scope.ServiceProvider.GetRequiredService<Application.Capabilities.IBrandBrainReader>();
        var version = await reader.CaptureVersionAsync(seeded.BrandId);

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var campaign = new ContentCampaign(
            seeded.TenantId, seeded.BrandId, weekStart,
            CampaignTrigger.Manual, version.VersionId, 200_000_000);

        db.ContentCampaigns.Add(campaign);
        await db.SaveChangesAsync();

        return (scope, campaign.Id, new StrategistInput
        {
            Brand = version.Snapshot,
            RecentContent = [],
            WeekStart = weekStart,
        });
    }

    /// <summary>A plan with the given number of items, only the first four being valid.</summary>
    private static string Plan(int items)
    {
        (string Type, string Topic)[] all =
        [
            ("StaticPost", "Empty chairs when clients cancel late"),
            ("StaticPost", "Booking requests lost in a full inbox"),
            ("Carousel", "One shared diary across four barbers"),
            ("Reel", "A client books at midnight while you sleep"),
        ];

        var chosen = all.Take(items).Select(item => new
        {
            type = item.Type,
            topic = item.Topic,
            pillar = "problem-solution",
            objective = "Show what an unanswered message costs",
            publish_day = "Tuesday",
            fact_keys = Array.Empty<string>(),
        });

        return JsonSerializer.Serialize(new
        {
            theme = "Bookings that happen without you",
            items = chosen,
        });
    }

    private sealed class ScriptedModel(params string[] responses) : ILanguageModelClient
    {
        private int _call;

        public string? RefusalCategory { get; init; }

        public string LastUserPrompt { get; private set; } = string.Empty;

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
        {
            LastUserPrompt = request.User;

            var json = RefusalCategory is not null
                ? "{}"
                : responses[Math.Min(_call, responses.Length - 1)];

            _call++;

            return Task.FromResult(new LlmResponse
            {
                Json = json,
                ModelId = "scripted",
                Usage = new TokenUsage(1500, 400, 0, 0),
                DurationMs = 1200,
                CostMicroCents = 175_000,
                RefusalCategory = RefusalCategory,
            });
        }
    }
}
