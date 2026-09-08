using ContentPilot.Rendering.Contracts;

namespace ContentPilot.RendererTests;

/// <summary>
/// Builds a legal request for any template from its manifest alone. Driving fixtures off
/// the manifest means a new template is covered by every test here the moment it is added,
/// without anyone remembering to write a fixture for it.
/// </summary>
public static class SampleRequests
{
    private static readonly IReadOnlyDictionary<string, string> Copy = new Dictionary<string, string>
    {
        ["eyebrow"] = "For salons",
        ["headline"] = "Fill the empty slots in your week",
        ["hook"] = "Your chair sat empty for 6 hours last week",
        ["subhead"] = "Clients book themselves, day or night. You stop chasing messages.",
        ["cta"] = "Try it free",
        ["quote"] = "We stopped losing bookings to unanswered messages. Half of them now arrive after closing time.",
        ["attribution"] = "Ana Kovač",
        ["role"] = "Owner, Glow Studio",
        ["rating"] = "★★★★★",
    };

    public static RenderImageRequest For(TemplateManifest manifest, AspectRatio ratio) => new()
    {
        TemplateId = manifest.TemplateId,
        AspectRatio = ratio,
        ColorScheme = manifest.ColorSchemes[0],
        Brand = FixtureAssets.Brand(),
        Language = "en",
        Text = TextFor(manifest, atBudget: false),
        Assets = AssetsFor(manifest),
    };

    /// <summary>
    /// Copy padded to exactly the declared budget. This is the case that matters: if a
    /// template clips here, the manifest is lying to the copywriter.
    /// </summary>
    public static RenderImageRequest AtBudget(TemplateManifest manifest, AspectRatio ratio) =>
        For(manifest, ratio) with { Text = TextFor(manifest, atBudget: true) };

    private static Dictionary<string, string> TextFor(TemplateManifest manifest, bool atBudget)
    {
        var text = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var slot in manifest.TextSlots)
        {
            var sample = Copy.TryGetValue(slot.Id, out var value) ? value : slot.Role;
            var budget = slot.BudgetFor("en");

            text[slot.Id] = atBudget ? Pad(sample, budget) : Trim(sample, budget);
        }

        return text;
    }

    private static Dictionary<string, ImagePayload> AssetsFor(TemplateManifest manifest)
    {
        var assets = new Dictionary<string, ImagePayload>(StringComparer.Ordinal);

        foreach (var slot in manifest.AssetSlots)
        {
            assets[slot.Id] = slot.Kind switch
            {
                AssetKind.ProductScreenshot => FixtureAssets.ProductScreenshot(),
                AssetKind.Logo => FixtureAssets.Logo(),
                AssetKind.Background => FixtureAssets.Background(),
                AssetKind.Photo => FixtureAssets.Logo(),
                _ => FixtureAssets.Logo(),
            };
        }

        return assets;
    }

    /// <summary>
    /// Pads with real words rather than a repeated character: word length drives line
    /// breaking, and a single long token would wrap in a way no real copy ever does.
    /// </summary>
    private static string Pad(string sample, int budget)
    {
        if (sample.Length >= budget)
        {
            return Trim(sample, budget);
        }

        var filler = new[] { "every", "single", "week", "without", "chasing", "anyone", "again", "for", "your", "team" };
        var result = sample;
        var i = 0;

        while (result.Length < budget)
        {
            var next = " " + filler[i++ % filler.Length];

            if (result.Length + next.Length > budget)
            {
                break;
            }

            result += next;
        }

        return result;
    }

    private static string Trim(string sample, int budget) =>
        sample.Length <= budget ? sample : sample[..budget].TrimEnd();
}
