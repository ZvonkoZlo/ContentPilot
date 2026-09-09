using ContentPilot.Application.Agents;
using ContentPilot.Application.Brand;
using ContentPilot.Domain.Branding;
using ContentPilot.Domain.Content;
using ContentPilot.Rendering.Contracts;
using ContentPilot.UnitTests.BrandBrain;
using Shouldly;
using ContractAssetKind = ContentPilot.Rendering.Contracts.AssetKind;

namespace ContentPilot.UnitTests.Agents;

/// <summary>
/// Eligibility is a computation, ranking is a judgement, and this is the computation. It is
/// what stops a template that needs a product screenshot from ever being offered to a brand
/// that has never uploaded one — by never showing it that option rather than by asking the
/// model nicely.
/// </summary>
public sealed class TemplateSelectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 6, 0, 0, TimeSpan.Zero);

    private static TemplateManifest Template(
        string id,
        bool needsScreenshot = false,
        bool safeMode = false,
        string[]? pillars = null,
        AspectRatio[]? ratios = null,
        TemplateContentType[]? contentTypes = null) => new()
    {
        TemplateId = id,
        Version = 1,
        Name = id,
        ContentTypes = contentTypes ?? [TemplateContentType.Static],
        AspectRatios = ratios ?? [AspectRatio.FourFive, AspectRatio.OneOne],
        PillarAffinity = pillars ?? [],
        SafeMode = safeMode,
        TextSlots = [new TextSlot { Id = "headline", Role = "headline", MaxChars = 60, MaxLines = 3 }],
        AssetSlots = needsScreenshot
            ?
            [
                new AssetSlot
                {
                    Id = "screenshot",
                    Kind = ContractAssetKind.ProductScreenshot,
                    Required = true,
                    Immutable = true,
                },
            ]
            : [],
    };

    private static BrandSnapshot Brand(params AssetView[] assets) =>
        BrandBrainAssembler.Assemble(Fixture.Input(), Now) with { Assets = assets };

    private static AssetView Asset(
        string kind = nameof(global::ContentPilot.Domain.Branding.AssetKind.ProductScreenshot),
        string origin = nameof(AssetOrigin.Upload),
        int width = 828,
        int height = 1792) => new()
    {
        Id = Guid.NewGuid(),
        Kind = kind,
        FileName = "shot.png",
        Width = width,
        Height = height,
        Origin = origin,
    };

    [Fact]
    public void A_template_needing_a_screenshot_is_offered_when_the_brand_has_one()
    {
        var result = TemplateSelector.Select(
            [Template("phone-floating", needsScreenshot: true)],
            ContentItemType.StaticPost, "problem-solution", Brand(Asset()));

        var candidate = result.Candidates.ShouldHaveSingleItem();
        candidate.AssetAssignments.ShouldContainKey("screenshot");
        candidate.Reference.ShouldBe("phone-floating@v1");
    }

    [Fact]
    public void A_template_needing_a_screenshot_is_refused_when_the_brand_has_none()
    {
        var result = TemplateSelector.Select(
            [Template("phone-floating", needsScreenshot: true)],
            ContentItemType.StaticPost, "problem-solution", Brand());

        result.Candidates.ShouldBeEmpty();

        // Not dropped silently: "why was nothing eligible" is a question an operator asks,
        // and the answer has to be better than a shrug.
        result.Rejections.ShouldHaveSingleItem().Reason.ShouldContain("screenshot");
    }

    [Fact]
    public void A_generated_image_can_never_fill_an_immutable_slot()
    {
        // The one failure this system must never have: a slot that promises the real
        // product showing something a model drew. Prevented by construction.
        var result = TemplateSelector.Select(
            [Template("phone-floating", needsScreenshot: true)],
            ContentItemType.StaticPost, "problem-solution",
            Brand(Asset(origin: nameof(AssetOrigin.Generated))));

        result.Candidates.ShouldBeEmpty();
        result.Rejections.ShouldHaveSingleItem();
    }

    [Fact]
    public void The_largest_screenshot_wins_because_scaling_down_keeps_detail()
    {
        var small = Asset(width: 400, height: 800);
        var large = Asset(width: 1170, height: 2532);

        var candidate = TemplateSelector.Select(
                [Template("phone-floating", needsScreenshot: true)],
                ContentItemType.StaticPost, "problem-solution", Brand(small, large))
            .Candidates.ShouldHaveSingleItem();

        candidate.AssetAssignments["screenshot"].Id.ShouldBe(large.Id);
    }

    [Fact]
    public void A_template_for_the_wrong_content_type_is_refused()
    {
        var result = TemplateSelector.Select(
            [Template("reel-only", contentTypes: [TemplateContentType.ReelScene])],
            ContentItemType.StaticPost, "problem-solution", Brand());

        result.Candidates.ShouldBeEmpty();
        result.Rejections.ShouldHaveSingleItem().Reason.ShouldContain("Static");
    }

    [Fact]
    public void A_required_ratio_the_template_cannot_render_is_refused()
    {
        var result = TemplateSelector.Select(
            [Template("square-only", ratios: [AspectRatio.OneOne])],
            ContentItemType.StaticPost, "problem-solution", Brand(),
            preferredRatio: AspectRatio.NineSixteen);

        result.Candidates.ShouldBeEmpty();
        result.Rejections.ShouldHaveSingleItem().Reason.ShouldContain("NineSixteen");
    }

    [Fact]
    public void Safe_mode_is_offered_last_rather_than_first()
    {
        var result = TemplateSelector.Select(
            [Template("safe-mode", safeMode: true), Template("hook-overlay")],
            ContentItemType.StaticPost, "problem-solution", Brand());

        // It is the escalation ladder's final rung. Ranking it first would quietly make
        // every week look the same.
        result.Candidates.Count.ShouldBe(2);
        result.Candidates[0].Template.TemplateId.ShouldBe("hook-overlay");
        result.Candidates[^1].Template.TemplateId.ShouldBe("safe-mode");
    }

    [Fact]
    public void A_matching_pillar_sorts_ahead_as_a_tie_break()
    {
        var result = TemplateSelector.Select(
            [Template("aaa-generic"), Template("zzz-matching", pillars: ["problem-solution"])],
            ContentItemType.StaticPost, "problem-solution", Brand());

        // Only an ordering hint. Both stay eligible; the director still decides.
        result.Candidates[0].Template.TemplateId.ShouldBe("zzz-matching");
        result.Candidates.Count.ShouldBe(2);
    }

    [Fact]
    public void Selection_is_deterministic_for_the_same_inputs()
    {
        var templates = new[] { Template("b"), Template("a"), Template("c") };
        var brand = Brand();

        var first = TemplateSelector.Select(templates, ContentItemType.StaticPost, "x", brand);
        var second = TemplateSelector.Select(templates, ContentItemType.StaticPost, "x", brand);

        first.Candidates.Select(c => c.Template.TemplateId)
            .ShouldBe(second.Candidates.Select(c => c.Template.TemplateId));
    }

    [Fact]
    public void An_optional_slot_is_filled_when_something_suits_and_skipped_otherwise()
    {
        var template = Template("hook-overlay") with
        {
            AssetSlots =
            [
                new AssetSlot { Id = "logo", Kind = ContractAssetKind.Logo, Required = false },
            ],
        };

        var withLogo = TemplateSelector.Select([template], ContentItemType.StaticPost, "x",
            Brand(Asset(kind: nameof(global::ContentPilot.Domain.Branding.AssetKind.Logo))));

        var withoutLogo = TemplateSelector.Select([template], ContentItemType.StaticPost, "x", Brand());

        withLogo.Candidates.ShouldHaveSingleItem().AssetAssignments.ShouldContainKey("logo");

        // An optional slot missing is not a reason to reject the template.
        withoutLogo.Candidates.ShouldHaveSingleItem().AssetAssignments.ShouldBeEmpty();
    }
}
