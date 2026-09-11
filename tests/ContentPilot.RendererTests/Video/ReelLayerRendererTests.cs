using ContentPilot.Renderer.Video;
using ImageMagick;
using Shouldly;

namespace ContentPilot.RendererTests.Video;

[Collection(ReelLayerCollection.Name)]
public sealed class ReelLayerRendererTests(ReelLayerFixture fixture)
{
    [RenderFact]
    public async Task Every_reel_template_renders_all_scenes_as_full_size_layers()
    {
        foreach (var manifest in fixture.Catalog.Manifests)
        {
            var spec = ReelSamples.For(manifest);
            var scenes = await fixture.RenderLayersAsync(manifest);

            var artifactDirectory = Path.Combine(
                ReelManifestTests.FindRepositoryRoot(), "artifacts", "render", "reels");
            Directory.CreateDirectory(artifactDirectory);
            await File.WriteAllBytesAsync(
                Path.Combine(artifactDirectory, $"{manifest.TemplateId}-cover.png"),
                scenes[0].CompositePng);

            scenes.Count.ShouldBe(manifest.Scenes.Count);

            foreach (var scene in scenes)
            {
                AssertImage(scene.BackgroundPng, 1080, 1920);
                AssertImage(scene.ProductPng, 1080, 1920);
                AssertImage(scene.TextPng, 1080, 1920);
                AssertImage(scene.CompositePng, 1080, 1920);

                scene.Report.Width.ShouldBe(1080);
                scene.Report.Height.ShouldBe(1920);
                scene.Report.BlockedRequests.ShouldBeEmpty();
                scene.Report.FontsLoaded.ShouldContain("Archivo");
                scene.Report.FontsLoaded.ShouldContain("Inter");

                var declared = manifest.FindScene(scene.SceneId)!;
                scene.Report.Slots.Select(slot => slot.SlotId).ShouldBe(
                    declared.TextSlots.Where(slot => spec.Scenes.Single(s => s.SceneId == scene.SceneId).Text.ContainsKey(slot.Id)).Select(slot => slot.Id)
                        .Concat(declared.AssetSlots.Where(slot => spec.Scenes.Single(s => s.SceneId == scene.SceneId).Assets.ContainsKey(slot.Id)).Select(slot => slot.Id)),
                    ignoreOrder: true);
            }
        }
    }

    [RenderFact]
    public async Task Text_stays_in_safe_areas_and_inside_declared_budgets()
    {
        foreach (var manifest in fixture.Catalog.Manifests)
        {
            var spec = ReelSamples.For(manifest);
            var scenes = await fixture.RenderLayersAsync(manifest);

            foreach (var scene in scenes)
            {
                var textSlots = scene.Report.Slots.Where(slot => slot.LineCount > 0).ToArray();
                var declared = manifest.FindScene(scene.SceneId)!;

                textSlots.Select(slot => slot.SlotId).ShouldBe(
                    declared.TextSlots.Where(slot => spec.Scenes.Single(s => s.SceneId == scene.SceneId).Text.ContainsKey(slot.Id))
                        .Select(slot => slot.Id),
                    ignoreOrder: true,
                    customMessage: $"{manifest.TemplateId}/{scene.SceneId} reported slots from another scene.");

                textSlots.Where(slot => slot.Overflows).ShouldBeEmpty(
                    $"{manifest.TemplateId}/{scene.SceneId} overflowed within its manifest budget.");
                textSlots.Where(slot => slot.BreaksSafeArea).ShouldBeEmpty(
                    $"{manifest.TemplateId}/{scene.SceneId} put readable text under platform chrome.");
            }
        }
    }

    [RenderFact]
    public async Task Immutable_product_screenshots_are_not_occluded()
    {
        foreach (var manifest in fixture.Catalog.Manifests.Where(m => !m.SafeMode))
        {
            var scenes = await fixture.RenderLayersAsync(manifest);

            foreach (var (rendered, declared) in scenes.Zip(manifest.Scenes))
            {
                foreach (var asset in declared.AssetSlots.Where(slot => slot.Immutable && slot.Required))
                {
                    var measured = rendered.Report.Slots.Single(slot => slot.SlotId == asset.Id);
                    measured.Occlusion.ShouldBeLessThanOrEqualTo(asset.MaxOcclusion);
                }
            }
        }
    }

    private static void AssertImage(byte[] png, uint width, uint height)
    {
        using var image = new MagickImage(png);
        image.Width.ShouldBe(width);
        image.Height.ShouldBe(height);
    }
}
