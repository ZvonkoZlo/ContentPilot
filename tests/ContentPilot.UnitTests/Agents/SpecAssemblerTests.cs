using ContentPilot.Application.Agents;
using ContentPilot.Application.Brand;
using ContentPilot.UnitTests.BrandBrain;
using ContentPilot.Rendering.Contracts;
using Shouldly;
using ContractAssetKind = ContentPilot.Rendering.Contracts.AssetKind;

namespace ContentPilot.UnitTests.Agents;

/// <summary>
/// SpecAssembly is arithmetic and lookups over decisions earlier steps already made — no
/// model call, and everything here is assertable without a renderer, a database, or object
/// storage in sight.
/// </summary>
public sealed class SpecAssemblerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 6, 0, 0, TimeSpan.Zero);

    private static TemplateManifest Manifest(bool requiresScreenshot = true) => new()
    {
        TemplateId = "phone-floating",
        Version = 3,
        Name = "Phone floating",
        ContentTypes = [TemplateContentType.Static],
        AspectRatios = [AspectRatio.FourFive, AspectRatio.OneOne],
        TextSlots =
        [
            new TextSlot { Id = "headline", Role = "headline", MaxChars = 60, MaxLines = 3 },
        ],
        AssetSlots = requiresScreenshot
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

    private static BrandSnapshot Brand() => BrandBrainAssembler.Assemble(Fixture.Input(), Now);

    private static CopySet Copy() => new()
    {
        Slots = [new CopySlot { Id = "headline", Text = "Fill the empty slots in your week" }],
        FactCitations = ["public-booking-page"],
    };

    private static ImagePayload Payload() => new() { MediaType = "image/png", Base64 = "AA==" };

    [Fact]
    public void The_request_carries_the_templates_id_and_version()
    {
        var request = SpecAssembler.Assemble(
            Manifest(), AspectRatio.FourFive, Copy(), Brand(), "en",
            new Dictionary<string, ImagePayload> { ["screenshot"] = Payload() });

        request.TemplateId.ShouldBe("phone-floating");
        request.TemplateVersion.ShouldBe(3);
        request.AspectRatio.ShouldBe(AspectRatio.FourFive);
    }

    [Fact]
    public void Copy_text_lands_on_the_matching_slot_id()
    {
        var request = SpecAssembler.Assemble(
            Manifest(), AspectRatio.FourFive, Copy(), Brand(), "en",
            new Dictionary<string, ImagePayload> { ["screenshot"] = Payload() });

        request.Text["headline"].ShouldBe("Fill the empty slots in your week");
    }

    [Fact]
    public void An_unsupported_aspect_ratio_is_refused()
    {
        Should.Throw<ArgumentException>(() => SpecAssembler.Assemble(
            Manifest(), AspectRatio.NineSixteen, Copy(), Brand(), "en",
            new Dictionary<string, ImagePayload> { ["screenshot"] = Payload() }));
    }

    [Fact]
    public void A_required_asset_slot_with_no_resolved_bytes_is_refused()
    {
        var ex = Should.Throw<ArgumentException>(() => SpecAssembler.Assemble(
            Manifest(), AspectRatio.FourFive, Copy(), Brand(), "en",
            new Dictionary<string, ImagePayload>()));

        ex.Message.ShouldContain("screenshot");
    }

    [Fact]
    public void A_template_with_no_required_assets_needs_none_supplied()
    {
        Should.NotThrow(() => SpecAssembler.Assemble(
            Manifest(requiresScreenshot: false), AspectRatio.FourFive, Copy(), Brand(), "en",
            new Dictionary<string, ImagePayload>()));
    }

    [Fact]
    public void The_brand_tokens_are_carried_over_from_the_visual_identity()
    {
        var brand = Brand();

        var request = SpecAssembler.Assemble(
            Manifest(), AspectRatio.FourFive, Copy(), brand, "en",
            new Dictionary<string, ImagePayload> { ["screenshot"] = Payload() });

        request.Brand.Name.ShouldBe(brand.Name);
        request.Brand.PrimaryColor.ShouldBe(brand.Visual.PrimaryColor);
        request.Brand.HeadingFont.ShouldBe(brand.Visual.HeadingFont);
    }

    [Fact]
    public void The_default_color_scheme_comes_from_the_manifest()
    {
        var manifest = Manifest(requiresScreenshot: false) with { ColorSchemes = [ColorScheme.DarkOnLight] };

        var request = SpecAssembler.Assemble(
            manifest, AspectRatio.FourFive, Copy(), Brand(), "en", new Dictionary<string, ImagePayload>());

        request.ColorScheme.ShouldBe(ColorScheme.DarkOnLight);
    }

    [Fact]
    public void An_explicit_color_scheme_overrides_the_manifests_default()
    {
        var request = SpecAssembler.Assemble(
            Manifest(requiresScreenshot: false), AspectRatio.FourFive, Copy(), Brand(), "en",
            new Dictionary<string, ImagePayload>(), colorScheme: ColorScheme.DarkOnLight);

        request.ColorScheme.ShouldBe(ColorScheme.DarkOnLight);
    }

    [Fact]
    public void Two_requests_built_from_slots_supplied_in_a_different_order_hash_identically()
    {
        var copyA = new CopySet
        {
            Slots =
            [
                new CopySlot { Id = "headline", Text = "Hi" },
                new CopySlot { Id = "sub", Text = "There" },
            ],
            FactCitations = [],
        };

        var copyB = new CopySet
        {
            Slots =
            [
                new CopySlot { Id = "sub", Text = "There" },
                new CopySlot { Id = "headline", Text = "Hi" },
            ],
            FactCitations = [],
        };

        var manifest = Manifest(requiresScreenshot: false) with
        {
            TextSlots =
            [
                new TextSlot { Id = "headline", Role = "headline", MaxChars = 60, MaxLines = 3 },
                new TextSlot { Id = "sub", Role = "body", MaxChars = 100, MaxLines = 3 },
            ],
        };

        var assets = new Dictionary<string, ImagePayload> { ["logo"] = Payload(), ["bg"] = Payload() };
        var assetsReordered = new Dictionary<string, ImagePayload> { ["bg"] = Payload(), ["logo"] = Payload() };

        var requestA = SpecAssembler.Assemble(manifest, AspectRatio.FourFive, copyA, Brand(), "en", assets);
        var requestB = SpecAssembler.Assemble(manifest, AspectRatio.FourFive, copyB, Brand(), "en", assetsReordered);

        SpecAssembler.ComputeHash(requestA).ShouldBe(SpecAssembler.ComputeHash(requestB));
    }

    [Fact]
    public void A_different_headline_produces_a_different_hash()
    {
        var manifest = Manifest(requiresScreenshot: false);
        var assets = new Dictionary<string, ImagePayload>();

        var requestA = SpecAssembler.Assemble(manifest, AspectRatio.FourFive, Copy(), Brand(), "en", assets);

        var differentCopy = new CopySet
        {
            Slots = [new CopySlot { Id = "headline", Text = "A totally different headline" }],
            FactCitations = [],
        };
        var requestB = SpecAssembler.Assemble(manifest, AspectRatio.FourFive, differentCopy, Brand(), "en", assets);

        SpecAssembler.ComputeHash(requestA).ShouldNotBe(SpecAssembler.ComputeHash(requestB));
    }
}
