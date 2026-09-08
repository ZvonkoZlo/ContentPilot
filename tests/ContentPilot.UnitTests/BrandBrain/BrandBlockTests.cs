using ContentPilot.Application.Brand;
using ContentPilot.Domain.Branding;
using Shouldly;

namespace ContentPilot.UnitTests.BrandBrain;

/// <summary>
/// The block is what every agent actually reads. Its two properties are load-bearing:
/// it must not move between runs, and it must not grow without limit — a block that
/// crowds out the instructions fails silently, because the model simply pays less
/// attention to the far end of a long prompt.
/// </summary>
public sealed class BrandBlockTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 6, 0, 0, TimeSpan.Zero);

    private static BrandSnapshot Snapshot(BrandBrainAssembler.Input? input = null) =>
        BrandBrainAssembler.Assemble(input ?? Fixture.Input(), Now);

    [Fact]
    public void The_same_snapshot_renders_the_same_bytes()
    {
        var snapshot = Snapshot();

        BrandBlockRenderer.Render(snapshot).ShouldBe(BrandBlockRenderer.Render(snapshot));
    }

    [Fact]
    public void The_block_stays_inside_its_token_budget()
    {
        var block = BrandBlockRenderer.Render(Snapshot());

        BrandBlockRenderer.EstimateTokens(block)
            .ShouldBeLessThan(BrandBlockRenderer.DefaultTokenBudget);
    }

    [Fact]
    public void A_brand_with_far_too_much_detail_is_still_budgeted()
    {
        var input = Fixture.Input();

        var manyFacts = Enumerable.Range(0, 200)
            .Select(i => Fixture.Fact($"fact-{i:D3}", $"Statement number {i} about the product, at realistic length.", FactCategory.Feature))
            .ToList();

        var block = BrandBlockRenderer.Render(Snapshot(input with { Facts = manyFacts }));

        // Trimming rather than truncating: the block says how many were omitted, so the
        // operator can see the brain has outgrown the prompt.
        BrandBlockRenderer.EstimateTokens(block).ShouldBeLessThan(BrandBlockRenderer.DefaultTokenBudget);
        block.ShouldContain("further facts omitted");
    }

    [Fact]
    public void Prohibitions_survive_trimming()
    {
        var input = Fixture.Input();

        var manyFacts = Enumerable.Range(0, 200)
            .Select(i => Fixture.Fact($"fact-{i:D3}", $"Statement {i}.", FactCategory.Feature))
            .ToList();

        var block = BrandBlockRenderer.Render(Snapshot(input with { Facts = manyFacts }));

        // Dropping a forbidden claim to save tokens is how a brand publishes something it
        // is legally exposed on. These are never the thing that gets cut.
        block.ShouldContain("Never make these claims");
        block.ShouldContain("cheapest");
        block.ShouldContain("Never use these words");
    }

    [Fact]
    public void Fact_keys_appear_so_copy_can_cite_them()
    {
        var block = BrandBlockRenderer.Render(Snapshot());

        block.ShouldContain("`whatsapp-reminders`");
        block.ShouldContain("Cite a fact key");
    }

    [Fact]
    public void Asset_identifiers_can_be_left_out_for_agents_that_do_not_choose_assets()
    {
        var snapshot = Snapshot();

        var withAssets = BrandBlockRenderer.Render(snapshot);
        var without = BrandBlockRenderer.Render(snapshot, BrandBlockOptions.Default with { IncludeAssets = false });

        withAssets.ShouldContain("Available assets");
        without.ShouldNotContain("Available assets");

        // Only the creative director picks assets; the others pay for those lines for nothing.
        without.Length.ShouldBeLessThan(withAssets.Length);
    }

    [Fact]
    public void The_primary_persona_is_marked_so_the_strategist_knows_who_to_write_for()
    {
        var block = BrandBlockRenderer.Render(Snapshot());

        block.ShouldContain("(primary)");
        block.ShouldContain("Their words");
    }

    [Fact]
    public void An_empty_brand_still_renders_a_valid_block()
    {
        var block = BrandBlockRenderer.Render(
            Snapshot(Fixture.Input() with { Profile = null, Facts = [], Personas = [], Assets = [] }));

        block.ShouldContain("## Brand: Appointso");
        block.ShouldNotContain("### Product facts");
    }

    [Fact]
    public void Both_languages_are_declared_when_a_brand_has_more_than_one()
    {
        var block = BrandBlockRenderer.Render(Snapshot());

        block.ShouldContain("Language: en");
        block.ShouldContain("also hr");
    }
}
