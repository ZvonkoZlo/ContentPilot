using ContentPilot.Application.Agents;
using ContentPilot.Application.Prompts;
using ContentPilot.UnitTests.BrandBrain;
using Shouldly;

namespace ContentPilot.UnitTests.Agents;

/// <summary>
/// Same shape as <see cref="ContentStrategistAgentTests"/>: the agent is a pure function of
/// its input, so what it puts in front of the model and what it accepts back are both
/// assertable with no key, no network, no database.
/// </summary>
public sealed class CopywriterAgentTests
{
    private static readonly CopywriterAgent Agent = new();
    private static readonly PromptLibrary Prompts = PromptLibrary.LoadEmbedded();
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 6, 0, 0, TimeSpan.Zero);

    private static CopywriterInput Input(
        IReadOnlyList<CopySlotBrief>? slots = null,
        IReadOnlyList<string>? factKeys = null) => new()
    {
        Brand = Application.Brand.BrandBrainAssembler.Assemble(Fixture.Input(), Now),
        Topic = "Your chair sits empty when someone cancels at 9pm",
        Pillar = "problem-solution",
        Objective = "Show that booking keeps filling the slot automatically.",
        AllowedFactKeys = factKeys ?? ["public-booking-page"],
        Slots = slots ?? DefaultSlots,
        Language = "en",
    };

    private static readonly CopySlotBrief[] DefaultSlots =
    [
        new() { Id = "headline", Role = "headline", MaxChars = 60, MaxLines = 3 },
        new() { Id = "subhead", Role = "body", MaxChars = 140, MaxLines = 4 },
    ];

    private static CopySet Output(params (string Id, string Text)[] slots) => new()
    {
        Slots = [.. slots.Select(s => new CopySlot { Id = s.Id, Text = s.Text })],
        FactCitations = [],
    };

    [Fact]
    public void Its_variables_satisfy_its_prompt_exactly()
    {
        var variables = Agent.BuildVariables(Input());

        Should.NotThrow(() => Prompts.Get(Agent.PromptId).Render(variables));
    }

    [Fact]
    public void The_prompt_is_registered_under_the_configured_copywriter_profile()
    {
        Prompts.Get(Agent.PromptId).ModelProfile.ShouldBe("copywriter");
    }

    [Fact]
    public void Every_slot_appears_in_the_rendered_brief()
    {
        var variables = Agent.BuildVariables(Input());

        variables["slots"].ShouldContain("headline");
        variables["slots"].ShouldContain("subhead");
        variables["slots"].ShouldContain("60 characters");
    }

    [Fact]
    public void No_fact_keys_reads_as_an_explicit_none_rather_than_a_blank()
    {
        var variables = Agent.BuildVariables(Input(factKeys: []));

        variables["fact_keys"].ShouldContain("none");
    }

    [Fact]
    public void Complete_copy_within_budget_validates_clean()
    {
        var output = Output(("headline", "Fill the empty slots in your week"), ("subhead", "Clients book themselves."));

        Agent.Validate(output, Input()).ShouldBeEmpty();
    }

    [Fact]
    public void Copy_over_budget_is_rejected()
    {
        var output = Output(("headline", new string('x', 61)), ("subhead", "fine"));

        Agent.Validate(output, Input()).ShouldContain(p => p.Contains("60"));
    }

    [Fact]
    public void A_missing_slot_is_rejected()
    {
        var output = Output(("headline", "Fill the empty slots"));

        Agent.Validate(output, Input()).ShouldContain(p => p.Contains("subhead"));
    }

    [Fact]
    public void A_citation_outside_the_allowed_fact_keys_is_rejected()
    {
        var output = new CopySet
        {
            Slots = [.. DefaultSlots.Select(s => new CopySlot { Id = s.Id, Text = "fine" })],
            FactCitations = ["not-a-real-key"],
        };

        Agent.Validate(output, Input()).ShouldContain(p => p.Contains("not-a-real-key"));
    }

    [Fact]
    public void A_banned_word_from_the_brand_voice_is_rejected()
    {
        // Fixture.Profile() bans "revolutionary" and "seamless".
        var output = Output(("headline", "A revolutionary way to book"), ("subhead", "fine"));

        Agent.Validate(output, Input()).ShouldContain(p => p.Contains("revolutionary"));
    }
}
