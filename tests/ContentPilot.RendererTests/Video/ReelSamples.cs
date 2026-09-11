using ContentPilot.Rendering.Contracts;
using ContentPilot.Renderer.Video;

namespace ContentPilot.RendererTests.Video;

internal static class ReelSamples
{
    public static ReelSpec For(ReelTemplateManifest manifest)
    {
        var scenes = manifest.Scenes.Select(scene => new ReelSceneSpec
        {
            SceneId = scene.Id,
            DurationSeconds = scene.DefaultDurationSeconds,
            Text = scene.TextSlots.ToDictionary(
                slot => slot.Id,
                slot => SampleText(slot.Id, slot.BudgetFor("en")),
                StringComparer.Ordinal),
            Assets = scene.AssetSlots.ToDictionary(
                slot => slot.Id,
                slot => slot.Kind switch
                {
                    AssetKind.ProductScreenshot => FixtureAssets.ProductScreenshot(),
                    AssetKind.Background => FixtureAssets.Background(1080, 1920),
                    _ => FixtureAssets.Logo(),
                },
                StringComparer.Ordinal),
        }).ToArray();

        return new ReelSpec
        {
            TemplateId = manifest.TemplateId,
            TemplateVersion = manifest.Version,
            DurationSeconds = Timeline.Duration(scenes, manifest.Scenes),
            Brand = FixtureAssets.Brand(),
            Language = "en",
            ColorScheme = manifest.ColorSchemes[0],
            Scenes = scenes,
        };
    }

    private static string SampleText(string slotId, int budget)
    {
        var value = slotId switch
        {
            "eyebrow" => "For busy salons",
            "headline" => "Turn empty hours into confirmed bookings",
            "stat" => "6 hours lost",
            "body" => "Clients book themselves while your team focuses on the work.",
            "step" => "Step one",
            "label" => "Before",
            "cta" => "Try it free",
            _ => "Clear, useful copy",
        };

        return value.Length <= budget ? value : value[..budget].TrimEnd();
    }
}
