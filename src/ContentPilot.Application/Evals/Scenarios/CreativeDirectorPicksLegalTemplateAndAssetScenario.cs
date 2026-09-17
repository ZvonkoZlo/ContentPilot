using ContentPilot.Application.Agents;
using ContentPilot.Application.Brand;
using ContentPilot.Domain.Branding;
using ContentPilot.Domain.Content;
using ContentPilot.Domain.Evals;
using ContentPilot.Rendering.Contracts;
using ContractAssetKind = ContentPilot.Rendering.Contracts.AssetKind;

namespace ContentPilot.Application.Evals.Scenarios;

/// <summary>
/// §27's catalogue: the creative director may see only templates that support the requested
/// content type/ratio and whose required slots can be filled by real assets of the right
/// kind. An immutable product screenshot must never be filled by a generated image.
/// </summary>
public sealed class CreativeDirectorPicksLegalTemplateAndAssetScenario : IEvalScenario
{
    public string Name => "CreativeDirector picks a legal template and asset";

    public EvalScenarioKind Kind => EvalScenarioKind.Deterministic;

    public Task<EvalScenarioOutcome> RunAsync(CancellationToken ct)
    {
        var uploadedScreenshot = Asset(nameof(Domain.Branding.AssetKind.ProductScreenshot), nameof(AssetOrigin.Upload), 900, 1600);
        var generatedScreenshot = Asset(nameof(Domain.Branding.AssetKind.ProductScreenshot), nameof(AssetOrigin.Generated), 1200, 2000);
        var brand = Brand(uploadedScreenshot, generatedScreenshot);
        var templates = new[]
        {
            Template("legal-screenshot", TemplateContentType.Static, AspectRatio.FourFive,
                new AssetSlot { Id = "screenshot", Kind = ContractAssetKind.ProductScreenshot, Required = true, Immutable = true }),
            Template("wrong-content-type", TemplateContentType.ReelScene, AspectRatio.FourFive),
            Template("missing-logo", TemplateContentType.Static, AspectRatio.FourFive,
                new AssetSlot { Id = "logo", Kind = ContractAssetKind.Logo, Required = true }),
            Template("wrong-ratio", TemplateContentType.Static, AspectRatio.OneOne),
        };

        var result = TemplateSelector.Select(
            templates, ContentItemType.StaticPost, "feature-highlight", brand, AspectRatio.FourFive);

        if (result.Candidates.Count != 1 || result.Candidates[0].Template.TemplateId != "legal-screenshot")
        {
            return Task.FromResult(EvalScenarioOutcome.Fail(
                $"Expected only legal-screenshot, got [{string.Join(", ", result.Candidates.Select(c => c.Template.TemplateId))}]."));
        }

        var candidate = result.Candidates[0];
        if (!candidate.AssetAssignments.TryGetValue("screenshot", out var assigned)
            || assigned.Id != uploadedScreenshot.Id
            || assigned.Kind != nameof(Domain.Branding.AssetKind.ProductScreenshot)
            || assigned.Origin != nameof(AssetOrigin.Upload))
        {
            return Task.FromResult(EvalScenarioOutcome.Fail(
                "The legal template did not receive the uploaded ProductScreenshot in its immutable screenshot slot."));
        }

        if (result.Candidates.Count + result.Rejections.Count != templates.Length)
        {
            return Task.FromResult(EvalScenarioOutcome.Fail(
                "At least one template disappeared without becoming either a candidate or an explained rejection."));
        }

        return Task.FromResult(EvalScenarioOutcome.Pass(
            "Only the compatible template was offered; its immutable slot used the uploaded screenshot, and every illegal template had an explained rejection."));
    }

    private static TemplateManifest Template(
        string id,
        TemplateContentType contentType,
        AspectRatio ratio,
        params AssetSlot[] assetSlots) => new()
    {
        TemplateId = id,
        Version = 1,
        Name = id,
        ContentTypes = [contentType],
        AspectRatios = [ratio],
        TextSlots = [new TextSlot { Id = "headline", Role = "headline", MaxChars = 60, MaxLines = 3 }],
        AssetSlots = assetSlots,
    };

    private static AssetView Asset(string kind, string origin, int width, int height) => new()
    {
        Id = Guid.CreateVersion7(),
        Kind = kind,
        FileName = "fixture.png",
        Width = width,
        Height = height,
        Origin = origin,
    };

    private static BrandSnapshot Brand(params AssetView[] assets) => new()
    {
        BrandId = Guid.CreateVersion7(),
        Name = "Eval brand",
        PrimaryLanguage = "en",
        Languages = ["en"],
        TimeZoneId = "UTC",
        Visual = VisualIdentity.Fallback,
        Voice = ToneOfVoice.Fallback,
        Messaging = Messaging.Fallback,
        Facts = [],
        Personas = [],
        Quota = new QuotaView { Posts = 1, Carousels = 0, Reels = 0 },
        Assets = assets,
    };
}
