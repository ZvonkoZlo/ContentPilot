using ContentPilot.Rendering.Contracts;
using ImageMagick;
using Shouldly;

namespace ContentPilot.RendererTests;

/// <summary>
/// The calibration corpus from the plan, in miniature: a clean render must pass, and each
/// deliberately injected defect must be caught. It doubles as the regression guard for the
/// thresholds — a renderer change that stops separating good from mutated fails here.
/// <para>
/// The corpus grows in Phase 4 with real Appointso screenshots at every supported scale.
/// What is asserted here is that the mechanism separates the two populations at all.
/// </para>
/// </summary>
[Collection(RenderCollection.Name)]
public sealed class FidelityTests(RenderFixture fixture)
{
    private async Task<(RenderImageResponse Response, ImagePayload Reference)> RenderAsync()
    {
        var manifest = fixture.Catalog.Get("phone-floating").Manifest;
        var request = SampleRequests.For(manifest, AspectRatio.FourFive);
        var response = await fixture.Renderer.RenderAsync(request, CancellationToken.None);

        return (response, request.Assets["screenshot"]);
    }

    private CompareRequest Build(RenderImageResponse response, ImagePayload reference, ImagePayload? rendered = null) => new()
    {
        SlotId = "screenshot",
        Reference = reference,
        Rendered = rendered ?? response.Image,
        Mask = response.Masks["screenshot"],
    };

    [RenderFact]
    public async Task A_clean_render_passes()
    {
        var (response, reference) = await RenderAsync();

        var result = fixture.Comparer.Compare(Build(response, reference));

        result.Verdict.ShouldBe(
            FidelityVerdict.Pass,
            $"Clean render rejected: {string.Join(" ", result.Reasons)} " +
            $"(ssim {result.Metrics.StructuralSimilarity:F3}, dE {result.Metrics.MeanDeltaE:F2}, " +
            $"occlusion {result.Metrics.Occlusion:P1}).");
    }

    [RenderFact]
    public async Task A_translucent_overlay_over_the_product_is_caught()
    {
        var (response, reference) = await RenderAsync();
        var box = SlotBox(response);

        // The classic scrim-over-UI mistake. Luminance-based similarity barely notices it,
        // which is exactly why the colour metric exists alongside it.
        var mutated = Mutate(response.Image, image =>
        {
            using var overlay = new MagickImage(new MagickColor(0, 0, 0), (uint)box.Width, (uint)box.Height);
            overlay.Alpha(AlphaOption.Set);
            overlay.Evaluate(Channels.Alpha, EvaluateOperator.Set, ushort.MaxValue * 0.35);
            image.Composite(overlay, (int)box.X, (int)box.Y, CompositeOperator.Over);
        });

        var result = fixture.Comparer.Compare(Build(response, reference, mutated));

        result.Verdict.ShouldNotBe(FidelityVerdict.Pass,
            $"A 35% scrim went unnoticed (ssim {result.Metrics.StructuralSimilarity:F3}, dE {result.Metrics.MeanDeltaE:F2}).");
    }

    [RenderFact]
    public async Task A_blurred_product_screenshot_is_caught()
    {
        var (response, reference) = await RenderAsync();
        var box = SlotBox(response);

        var mutated = Mutate(response.Image, image =>
        {
            using var region = CropRegion(image, box);
            region.Blur(0, 3);
            image.Composite(region, (int)box.X, (int)box.Y, CompositeOperator.Over);
        });

        var result = fixture.Comparer.Compare(Build(response, reference, mutated));

        result.Verdict.ShouldNotBe(FidelityVerdict.Pass,
            $"A blurred UI passed (ssim {result.Metrics.StructuralSimilarity:F3}).");
    }

    [RenderFact]
    public async Task A_tinted_product_screenshot_is_caught()
    {
        var (response, reference) = await RenderAsync();
        var box = SlotBox(response);

        var mutated = Mutate(response.Image, image =>
        {
            using var region = CropRegion(image, box);
            using var tint = new MagickImage(new MagickColor("#6C4CF1"), region.Width, region.Height);
            tint.Alpha(AlphaOption.Set);
            tint.Evaluate(Channels.Alpha, EvaluateOperator.Set, ushort.MaxValue * 0.22);
            region.Composite(tint, CompositeOperator.Over);
            image.Composite(region, (int)box.X, (int)box.Y, CompositeOperator.Over);
        });

        var result = fixture.Comparer.Compare(Build(response, reference, mutated));

        // Structural similarity on luminance is nearly blind to a uniform cast. If this
        // ever passes, the colour metric has stopped doing its job.
        result.Verdict.ShouldNotBe(FidelityVerdict.Pass,
            $"A brand-colour tint passed (dE {result.Metrics.MeanDeltaE:F2}).");
    }

    [RenderFact]
    public async Task A_completely_different_image_fails_outright()
    {
        var (response, reference) = await RenderAsync();
        var box = SlotBox(response);

        var mutated = Mutate(response.Image, image =>
        {
            using var wrong = new MagickImage(FixtureAssets.Background().ToBytes());
            wrong.Resize(new MagickGeometry((uint)box.Width, (uint)box.Height) { IgnoreAspectRatio = true });
            image.Composite(wrong, (int)box.X, (int)box.Y, CompositeOperator.Over);
        });

        var result = fixture.Comparer.Compare(Build(response, reference, mutated));

        result.Verdict.ShouldBe(FidelityVerdict.Fail);
        result.Reasons.ShouldNotBeEmpty();
    }

    [RenderFact]
    public async Task Legitimate_downscaling_is_not_mistaken_for_tampering()
    {
        var (response, reference) = await RenderAsync();

        var referenceImage = new MagickImage(reference.ToBytes());
        var box = SlotBox(response);

        // The reference is roughly 828x1792 and lands in a ~300x600 slot. A naive pixel
        // comparison fails on this alone, which is the whole reason both sides are
        // normalised through the same resampling before anything is measured.
        (referenceImage.Width / box.Width).ShouldBeGreaterThan(2);

        var result = fixture.Comparer.Compare(Build(response, reference));

        result.Verdict.ShouldBe(FidelityVerdict.Pass);
        result.Metrics.StructuralSimilarity.ShouldBeGreaterThan(0.97);

        referenceImage.Dispose();
    }

    [RenderFact]
    public async Task The_mask_measures_the_slot_box_itself()
    {
        var (response, reference) = await RenderAsync();

        var result = fixture.Comparer.Compare(Build(response, reference));
        var reported = SlotBox(response);

        result.Metrics.MeasuredBox.ShouldNotBeNull();

        // What the mask painted should match what the layout engine reported, within
        // rounding. A divergence means the template is doing something unexpected.
        Math.Abs(result.Metrics.MeasuredBox!.Width - reported.Width).ShouldBeLessThan(4);
        Math.Abs(result.Metrics.MeasuredBox.Height - reported.Height).ShouldBeLessThan(4);
    }

    [RenderFact]
    public async Task Jpeg_recompression_alone_does_not_fail_the_check()
    {
        var (response, reference) = await RenderAsync();

        var recompressed = Mutate(response.Image, image =>
        {
            image.Format = MagickFormat.Jpeg;
            image.Quality = 82;
        });

        var result = fixture.Comparer.Compare(Build(response, reference, recompressed));

        // Delivery formats are a normal part of the pipeline. Treating compression as
        // tampering would make the check useless the moment JPEG output is enabled.
        result.Verdict.ShouldNotBe(FidelityVerdict.Fail,
            $"JPEG at quality 82 read as tampering (ssim {result.Metrics.StructuralSimilarity:F3}, dE {result.Metrics.MeanDeltaE:F2}).");
    }

    private static BoundingBox SlotBox(RenderImageResponse response) =>
        response.Report.Slots.Single(s => s.SlotId == "screenshot").Box;

    private static MagickImage CropRegion(IMagickImage<ushort> image, BoundingBox box)
    {
        var clone = (MagickImage)image.Clone();
        clone.Crop(new MagickGeometry((int)box.X, (int)box.Y, (uint)box.Width, (uint)box.Height));
        clone.ResetPage();

        return clone;
    }

    private static ImagePayload Mutate(ImagePayload source, Action<MagickImage> mutation)
    {
        using var image = new MagickImage(source.ToBytes());
        mutation(image);

        var format = image.Format == MagickFormat.Jpeg ? MagickFormat.Jpeg : MagickFormat.Png;
        var mediaType = format == MagickFormat.Jpeg ? "image/jpeg" : "image/png";

        return ImagePayload.FromBytes(image.ToByteArray(format), mediaType);
    }
}
