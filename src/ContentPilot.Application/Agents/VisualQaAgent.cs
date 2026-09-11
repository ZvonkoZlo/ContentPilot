using ContentPilot.Application.Ai;
using ContentPilot.Application.Brand;
using ContentPilot.Domain.Quality;
using ContentPilot.Rendering.Contracts;

namespace ContentPilot.Application.Agents;

/// <summary>
/// Gate 2. Asked only what code cannot answer: does this look like a competent designer
/// made it, does generated imagery contain artefacts, is it on-brand, does the composition
/// read at thumbnail size. Everything measurable — overflow, contrast, occlusion, fidelity —
/// is <see cref="DeterministicQaSuite"/>'s job and never this one's; the validator refuses
/// any finding this agent reports that belongs to a different gate, so a vision model
/// claiming text overflow is a thrown exception, not a possibility to guard against later.
/// </summary>
public sealed class VisualQaAgent : IAgent<VisualQaInput, VisualQaOutput>
{
    public string Name => "visual-qa";

    public string Version => "1";

    public string PromptId => "visual-qa";

    public string ResponseSchema => VisualQaOutputSchema.Json;

    public IReadOnlyDictionary<string, string> BuildVariables(VisualQaInput input) =>
        new Dictionary<string, string>
        {
            ["brand_block"] = BrandBlockRenderer.Render(
                input.Brand,
                // The judge needs the brand's look and voice to say "off-brand", not its
                // facts or its assets — it never cites a claim or names an asset id.
                BrandBlockOptions.Default with { IncludeAssets = false, MaxFacts = 0 }),
        };

    /// <summary>
    /// Full size first, then the 150 px thumbnail — thumbnail legibility is how social
    /// content is actually consumed, and models judge it well only when shown it directly
    /// rather than asked to imagine it from the full image.
    /// </summary>
    public IReadOnlyList<LlmImageAttachment> BuildImages(VisualQaInput input) =>
        [ToAttachment(input.FullImage), ToAttachment(input.Thumbnail)];

    public IReadOnlyList<string> Validate(VisualQaOutput output, VisualQaInput input) =>
        VisualQaValidator.Validate(output);

    private static LlmImageAttachment ToAttachment(ImagePayload image) =>
        new() { MediaType = image.MediaType, Base64Data = image.Base64 };
}

public sealed record VisualQaInput
{
    /// <summary>The frozen snapshot — the same one every other agent in this campaign reads from.</summary>
    public required BrandSnapshot Brand { get; init; }

    public required ImagePayload FullImage { get; init; }

    /// <summary>A 150 px-wide render of the same image — how this content is actually scrolled past.</summary>
    public required ImagePayload Thumbnail { get; init; }
}
