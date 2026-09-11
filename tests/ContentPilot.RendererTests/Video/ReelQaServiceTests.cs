using ContentPilot.Rendering.Contracts;
using ContentPilot.Renderer.Video;
using Shouldly;

namespace ContentPilot.RendererTests.Video;

public sealed class ReelQaServiceTests
{
    private readonly ReelTemplateCatalog _catalog = ReelManifestTests.CreateCatalog();
    private readonly ReelQaService _qa = new();

    [Fact]
    public void Healthy_container_and_scene_reports_pass_every_deterministic_check()
    {
        var manifest = _catalog.Get("reel-feature-tour");
        var spec = ReelSamples.For(manifest);
        var rendered = HealthyScenes(spec, manifest);
        var keyframes = new[] { Keyframe(0, 0.02) };
        var probe = new VideoProbe(1080, 1920, 15, 30, true, -8, 0);

        var report = _qa.BuildReport(spec, manifest, rendered, keyframes, probe, 4_000);

        report.Passed.ShouldBeTrue();
        report.Checks.Count.ShouldBe(Enum.GetValues<ReelCheckCode>().Length);
        report.Scenes.Count.ShouldBe(spec.Scenes.Count);
        report.Scenes[^1].EndsAtSeconds.ShouldBe(15, tolerance: 0.002);
    }

    [Fact]
    public void Container_defects_are_reported_as_closed_check_codes()
    {
        var manifest = _catalog.Get("reel-problem-solution");
        var spec = ReelSamples.For(manifest);
        var rendered = HealthyScenes(spec, manifest);
        var probe = new VideoProbe(720, 1280, 14.5, 24, false, 0, 2);

        var report = _qa.BuildReport(spec, manifest, rendered, [Keyframe(0, 1)], probe, 4_000);

        report.Passed.ShouldBeFalse();
        report.Checks.Single(check => check.Code == ReelCheckCode.Duration).Passed.ShouldBeFalse();
        report.Checks.Single(check => check.Code == ReelCheckCode.FrameRate).Passed.ShouldBeFalse();
        report.Checks.Single(check => check.Code == ReelCheckCode.Resolution).Passed.ShouldBeFalse();
        report.Checks.Single(check => check.Code == ReelCheckCode.AudioTrack).Passed.ShouldBeFalse();
        report.Checks.Single(check => check.Code == ReelCheckCode.BlackFrames).Passed.ShouldBeFalse();
    }

    [Fact]
    public void Static_overflow_and_unsafe_text_fail_layout_check_without_a_model()
    {
        var manifest = _catalog.Get("reel-problem-solution");
        var spec = ReelSamples.For(manifest);
        var rendered = HealthyScenes(spec, manifest).ToArray();
        var first = rendered[0];
        var slots = first.Report.Slots.ToArray();
        slots[0] = slots[0] with { Overflows = true, BreaksSafeArea = true };
        rendered[0] = first with { Report = first.Report with { Slots = slots } };

        var report = _qa.BuildReport(
            spec,
            manifest,
            rendered,
            [Keyframe(0, 0)],
            new VideoProbe(1080, 1920, 15, 30, true, null, 0),
            4_000);

        var layout = report.Checks.Single(check => check.Code == ReelCheckCode.StaticLayout);
        layout.Passed.ShouldBeFalse();
        layout.Detail.ShouldContain("overflows");
        layout.Detail.ShouldContain("safe area");
    }

    private static IReadOnlyList<RenderedSceneLayers> HealthyScenes(
        ReelSpec spec,
        ReelTemplateManifest manifest) =>
        manifest.Scenes.Select((scene, index) => new RenderedSceneLayers(
            scene.Id,
            [],
            [],
            [],
            [],
            new RenderReport
            {
                Width = 1080,
                Height = 1920,
                DeviceScaleFactor = 2,
                FontsLoaded = [spec.Brand.HeadingFont, spec.Brand.BodyFont],
                RenderDurationMs = 100,
                Slots = scene.TextSlots.Select(slot => new SlotMeasurement
                    {
                        SlotId = slot.Id,
                        Box = new BoundingBox { X = 100, Y = 200, Width = 800, Height = 100 },
                        Overflows = false,
                        LineCount = 1,
                        FontSizePx = 64,
                        ForegroundColor = "rgb(255,255,255)",
                        ContrastRatio = 8,
                        BreaksSafeArea = false,
                    })
                    .Concat(scene.AssetSlots.Select(slot => new SlotMeasurement
                    {
                        SlotId = slot.Id,
                        Box = new BoundingBox { X = 100, Y = 500, Width = 800, Height = 900 },
                        Overflows = false,
                        LineCount = 0,
                        Occlusion = 0,
                        BreaksSafeArea = false,
                    }))
                    .ToArray(),
            })).ToArray();

    private static ReelKeyframe Keyframe(double timestamp, double blackFraction) => new()
    {
        TimestampSeconds = timestamp,
        Kind = ReelKeyframeKind.Cover,
        Image = ImagePayload.FromBytes([1], "image/png"),
        BlackPixelFraction = blackFraction,
        PerceptualHash = 1,
    };
}
