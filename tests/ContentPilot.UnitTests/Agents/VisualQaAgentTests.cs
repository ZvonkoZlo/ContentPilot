using ContentPilot.Application.Agents;
using ContentPilot.Application.Prompts;
using ContentPilot.Domain.Quality;
using ContentPilot.Rendering.Contracts;
using ContentPilot.UnitTests.BrandBrain;
using Shouldly;

namespace ContentPilot.UnitTests.Agents;

/// <summary>
/// Same shape as every other agent test in this suite: the agent is a pure function of its
/// input, so what it puts in front of the model and what it accepts back are both
/// assertable with no key, no network, no database.
/// </summary>
public sealed class VisualQaAgentTests
{
    private static readonly VisualQaAgent Agent = new();
    private static readonly PromptLibrary Prompts = PromptLibrary.LoadEmbedded();
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 6, 0, 0, TimeSpan.Zero);

    private static readonly ImagePayload Image = new() { MediaType = "image/png", Base64 = "AA==" };

    private static VisualQaInput Input() => new()
    {
        Brand = Application.Brand.BrandBrainAssembler.Assemble(Fixture.Input(), Now),
        FullImage = Image,
        Thumbnail = Image,
    };

    private static VisualQaOutput Output(params (QaFindingCode Code, QaSeverity Severity, double Confidence)[] findings) => new()
    {
        Findings = [.. findings.Select(f => new VisualQaFinding
        {
            Code = f.Code.ToString(),
            Severity = f.Severity.ToString(),
            Confidence = f.Confidence,
            Detail = "observed",
        })],
    };

    [Fact]
    public void Its_variables_satisfy_its_prompt_exactly()
    {
        var variables = Agent.BuildVariables(Input());

        Should.NotThrow(() => Prompts.Get(Agent.PromptId).Render(variables));
    }

    [Fact]
    public void The_prompt_is_registered_under_the_configured_visual_qa_profile()
    {
        Prompts.Get(Agent.PromptId).ModelProfile.ShouldBe("visual-qa");
    }

    [Fact]
    public void Both_images_are_attached_full_size_first_then_the_thumbnail()
    {
        var fullImage = new ImagePayload { MediaType = "image/png", Base64 = "FULL" };
        var thumbnail = new ImagePayload { MediaType = "image/png", Base64 = "THUMB" };

        var images = Agent.BuildImages(Input() with { FullImage = fullImage, Thumbnail = thumbnail });

        images.Count.ShouldBe(2);
        images[0].Base64Data.ShouldBe("FULL");
        images[1].Base64Data.ShouldBe("THUMB");
    }

    [Fact]
    public void An_empty_findings_list_validates_clean()
    {
        Agent.Validate(Output(), Input()).ShouldBeEmpty();
    }

    [Fact]
    public void A_real_finding_from_the_visual_band_validates_clean()
    {
        var output = Output((QaFindingCode.OffBrand, QaSeverity.Major, 0.8));

        Agent.Validate(output, Input()).ShouldBeEmpty();
    }

    [Fact]
    public void A_code_from_a_different_gate_is_rejected()
    {
        // TextOverflow is the deterministic gate's own — a vision model reaching for it
        // would put judgement in front of a measurement that already answered exactly.
        var output = new VisualQaOutput
        {
            Findings = [new VisualQaFinding { Code = "TextOverflow", Severity = "Blocking", Confidence = 0.9, Detail = "x" }],
        };

        Agent.Validate(output, Input()).ShouldContain(p => p.Contains("TextOverflow"));
    }

    [Fact]
    public void An_unknown_code_is_rejected()
    {
        var output = new VisualQaOutput
        {
            Findings = [new VisualQaFinding { Code = "NotARealCode", Severity = "Major", Confidence = 0.7, Detail = "x" }],
        };

        Agent.Validate(output, Input()).ShouldContain(p => p.Contains("NotARealCode"));
    }

    [Fact]
    public void A_confidence_outside_zero_to_one_is_rejected()
    {
        var output = new VisualQaOutput
        {
            Findings = [new VisualQaFinding { Code = "OffBrand", Severity = "Major", Confidence = 1.4, Detail = "x" }],
        };

        Agent.Validate(output, Input()).ShouldContain(p => p.Contains("confidence"));
    }
}
