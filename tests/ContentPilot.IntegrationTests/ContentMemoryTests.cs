using ContentPilot.Application.Abstractions;
using ContentPilot.Application.Agents;
using ContentPilot.Application.Brand;
using ContentPilot.Application.Capabilities;
using ContentPilot.Application.ContentMemory;
using ContentPilot.Domain.Content;
using ContentPilot.Infrastructure.Branding;
using ContentPilot.Infrastructure.Persistence;
using ContentPilot.IntegrationTests.Infrastructure;
using ContentPilot.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace ContentPilot.IntegrationTests;

/// <summary>
/// The memory serves two consumers with different appetites: the model needs a little recent
/// prose, the novelty check needs a long tail of hashes. These assert the split holds against
/// a real database.
/// </summary>
[Collection(ContentPilotCollection.Name)]
public sealed class ContentMemoryTests(ContentPilotFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 6, 0, 0, TimeSpan.Zero);

    /// <summary>Everything one test needs, so nothing has to be smuggled through a static.</summary>
    private sealed record Harness(
        AsyncServiceScope Scope, Guid TenantId, Guid BrandId, BrandSnapshot Snapshot)
    {
        public AppDbContext Db => Scope.ServiceProvider.GetRequiredService<AppDbContext>();

        public ContentMemoryReader Reader => Scope.ServiceProvider.GetRequiredService<ContentMemoryReader>();

        public void AddHistory(string topic, DateTimeOffset approvedAt) =>
            Db.ContentHistory.Add(new ContentHistoryEntry(
                TenantId, BrandId, Guid.NewGuid(), ContentItemType.StaticPost,
                topic, "problem-solution", "A hook",
                SimHash.Compute(topic), SimHash.Compute("A hook"), "hook-overlay", approvedAt));

        public StrategistInput Input(IReadOnlyList<RecentContent> recent) => new()
        {
            Brand = Snapshot,
            RecentContent = recent,
            WeekStart = new DateOnly(2026, 9, 14),
        };
    }

    [DockerFact]
    public async Task An_empty_history_reads_as_a_first_week()
    {
        var harness = await SetupAsync();
        await using var _ = harness.Scope;

        var recent = await harness.Reader.ReadAsync(harness.BrandId, Now);

        recent.ShouldBeEmpty();

        // The agent turns that into a sentence rather than an empty list, because a missing
        // section reads to a model as an omission rather than as "there is nothing yet".
        new ContentStrategistAgent()
            .BuildVariables(harness.Input(recent))["recent_content"]
            .ShouldContain("first week");
    }

    [DockerFact]
    public async Task Recent_entries_keep_their_text_and_older_ones_keep_only_their_hash()
    {
        var harness = await SetupAsync();
        await using var _ = harness.Scope;

        // Twenty weeks: more than the prompt window, inside the novelty window.
        for (var week = 1; week <= 20; week++)
        {
            harness.AddHistory($"Topic number {week} about bookings", Now.AddDays(-7 * week));
        }

        await harness.Db.SaveChangesAsync();

        var recent = await harness.Reader.ReadAsync(harness.BrandId, Now);

        recent.Count.ShouldBe(20);

        // Prose is expensive in a prompt; hashes are eight bytes and cost nothing.
        recent.Count(entry => entry.Topic.Length > 0).ShouldBe(ContentMemoryReader.PromptWindow);
        recent.ShouldAllBe(entry => entry.TopicHash != 0);

        // Newest first, so the model reads the most relevant history at the top.
        recent[0].Topic.ShouldBe("Topic number 1 about bookings");
    }

    [DockerFact]
    public async Task History_beyond_the_novelty_window_is_not_read_at_all()
    {
        var harness = await SetupAsync();
        await using var _ = harness.Scope;

        harness.AddHistory("Something said a long time ago", Now.AddDays(-400));
        harness.AddHistory("Something said recently", Now.AddDays(-7));
        await harness.Db.SaveChangesAsync();

        var recent = await harness.Reader.ReadAsync(harness.BrandId, Now);

        // A year-old topic is fair game again; repeating yourself only matters while anyone
        // still remembers.
        recent.ShouldHaveSingleItem().Topic.ShouldBe("Something said recently");
    }

    [DockerFact]
    public async Task The_prompt_shows_only_the_window_even_when_handed_more()
    {
        var harness = await SetupAsync();
        await using var _ = harness.Scope;

        for (var week = 1; week <= 20; week++)
        {
            harness.AddHistory($"Distinct subject {week}", Now.AddDays(-7 * week));
        }

        await harness.Db.SaveChangesAsync();

        var recent = await harness.Reader.ReadAsync(harness.BrandId, Now);
        var block = new ContentStrategistAgent().BuildVariables(harness.Input(recent))["recent_content"];

        block.Split('\n').Length.ShouldBe(ContentStrategistAgent.RecentContentShown);

        // Entries stripped for the novelty tail are skipped, not printed as blank rows.
        block.ShouldNotContain("· ·");
    }

    [DockerFact]
    public async Task A_repeat_is_caught_against_history_the_model_was_never_shown()
    {
        var harness = await SetupAsync();
        await using var _ = harness.Scope;

        // Pushed past the prompt window, so only its hash survives into the check.
        harness.AddHistory("An empty chair when clients cancel late at night", Now.AddDays(-140));

        for (var week = 1; week <= 15; week++)
        {
            harness.AddHistory($"Unrelated subject {week}", Now.AddDays(-7 * week));
        }

        await harness.Db.SaveChangesAsync();

        var recent = await harness.Reader.ReadAsync(harness.BrandId, Now);

        var plan = new WeeklyPlan
        {
            Theme = "Bookings that happen without you",
            Items =
            [
                Item("StaticPost", "Your chair sits empty when a client cancels late at night"),
                Item("StaticPost", "One shared diary across four barbers"),
                Item("Carousel", "Picking a service before choosing a time"),
                Item("Reel", "A booking page that never closes"),
            ],
        };

        // This is the point of carrying hashes the model never sees: it cannot avoid
        // repeating something it was not shown, so the check must remember further than the
        // prompt does.
        new ContentStrategistAgent().Validate(plan, harness.Input(recent))
            .ShouldContain(p => p.Contains("repeats recent content"));
    }

    /// <summary>
    /// A brand of its own per test. History accumulates against a brand, and these tests
    /// write a lot of it — sharing the seeded brand would make each test's result depend on
    /// which others had already run.
    /// </summary>
    private async Task<Harness> SetupAsync()
    {
        var scope = fixture.CreateScope();
        var seeded = await scope.ServiceProvider.GetRequiredService<GoldenTenantSeeder>().SeedAsync();

        scope.ServiceProvider.GetRequiredService<IMutableTenantContext>().SetTenant(seeded.TenantId);

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var brand = new Domain.Branding.Brand(
            seeded.TenantId, $"Memory test {Guid.NewGuid():N}"[..24], "Europe/Zagreb", ["en"]);

        db.Brands.Add(brand);
        await db.SaveChangesAsync();

        var snapshot = await scope.ServiceProvider
            .GetRequiredService<IBrandBrainReader>()
            .GetSnapshotAsync(brand.Id);

        return new Harness(scope, seeded.TenantId, brand.Id, snapshot);
    }

    private static PlannedItem Item(string type, string topic) => new()
    {
        Type = type,
        Topic = topic,
        Pillar = "problem-solution",
        Objective = "Show what an unanswered message costs",
        PublishDay = "Tuesday",
        FactKeys = [],
    };
}
