using ContentPilot.Application.Agents;
using ContentPilot.Application.Prompts;
using ContentPilot.UnitTests.BrandBrain;
using Shouldly;

namespace ContentPilot.UnitTests.Agents;

public sealed class MarketingQaAgentTests
{
    private static readonly MarketingQaAgent Agent = new();
    private static readonly PromptLibrary Prompts = PromptLibrary.LoadEmbedded();
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 6, 0, 0, TimeSpan.Zero);

    private static CopySet Copy(params string[] citations) => new()
    {
        Slots = [new CopySlot { Id = "headline", Text = "Fill the empty slots in your week" }],
        FactCitations = citations,
    };

    private static MarketingQaInput Input(CopySet? copy = null) => new()
    {
        Brand = Application.Brand.BrandBrainAssembler.Assemble(Fixture.Input(), Now),
        Topic = "Empty chairs when clients cancel late",
        Objective = "Show automatic rebooking.",
        Copy = copy ?? Copy(),
        RecentContent = [],
    };

    [Fact]
    public void Its_variables_satisfy_its_prompt_exactly()
    {
        var variables = Agent.BuildVariables(Input());

        Should.NotThrow(() => Prompts.Get(Agent.PromptId).Render(variables));
    }

    [Fact]
    public void The_prompt_is_registered_under_the_configured_marketing_qa_profile()
    {
        Prompts.Get(Agent.PromptId).ModelProfile.ShouldBe("marketing-qa");
    }

    [Fact]
    public void No_citations_reads_as_an_explicit_none_rather_than_a_blank()
    {
        var variables = Agent.BuildVariables(Input());

        variables["cited_facts"].ShouldContain("None cited");
    }

    [Fact]
    public void A_cited_fact_shows_its_actual_statement_not_only_its_key()
    {
        var variables = Agent.BuildVariables(Input(Copy("public-booking-page")));

        variables["cited_facts"].ShouldContain("public-booking-page");
        variables["cited_facts"].ShouldContain("Every business gets a public booking page."); // the fixture's fact statement text
    }

    [Fact]
    public void No_agent_has_the_wrong_default_for_images()
    {
        // MarketingQA is text-only — the default interface member on IAgent<,> must apply
        // unchanged, confirming this agent needs no vision wiring of its own.
        ((IAgent<MarketingQaInput, MarketingQaOutput>)Agent).BuildImages(Input()).ShouldBeEmpty();
    }

    [Fact]
    public void An_empty_findings_list_validates_clean()
    {
        var output = new MarketingQaOutput { Findings = [] };

        Agent.Validate(output, Input()).ShouldBeEmpty();
    }

    [Fact]
    public void A_real_finding_from_the_marketing_band_validates_clean()
    {
        var output = new MarketingQaOutput
        {
            Findings = [new MarketingQaFinding { Code = "WeakHook", Severity = "Major", Confidence = 0.75, Detail = "generic" }],
        };

        Agent.Validate(output, Input()).ShouldBeEmpty();
    }

    [Fact]
    public void A_code_from_a_different_gate_is_rejected()
    {
        var output = new MarketingQaOutput
        {
            Findings = [new MarketingQaFinding { Code = "OffBrand", Severity = "Major", Confidence = 0.7, Detail = "x" }],
        };

        Agent.Validate(output, Input()).ShouldContain(p => p.Contains("OffBrand"));
    }
}
