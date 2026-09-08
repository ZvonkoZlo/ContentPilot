using ContentPilot.Domain.Branding;

namespace ContentPilot.Application.Brand;

/// <summary>
/// Composes the scattered pieces of a Brand Brain into one frozen snapshot.
/// <para>
/// It is a pure function on purpose: given the same rows it returns the same object with
/// the same hash, so a campaign that starts twice against an unchanged brand reuses one
/// version rather than creating two indistinguishable ones.
/// </para>
/// </summary>
public static class BrandBrainAssembler
{
    public sealed record Input(
        Domain.Branding.Brand Brand,
        BrandProfile? Profile,
        IReadOnlyCollection<ProductFact> Facts,
        IReadOnlyCollection<AudiencePersona> Personas,
        ContentPreferences? Preferences,
        IReadOnlyCollection<BrandAsset> Assets,
        string? Industry = null);

    public static BrandSnapshot Assemble(Input input, DateTimeOffset asOf)
    {
        var brand = input.Brand;

        return new BrandSnapshot
        {
            BrandId = brand.Id,
            Name = brand.Name,
            Website = brand.Website,
            PrimaryLanguage = brand.PrimaryLanguage,
            Languages = [.. brand.Languages],
            TimeZoneId = brand.TimeZoneId,
            Industry = input.Industry,

            // A brand with no profile yet still has to be renderable, so the fallbacks are
            // real defaults rather than nulls the agents would have to handle.
            Visual = input.Profile?.Visual ?? VisualIdentity.Fallback,
            Voice = input.Profile?.Voice ?? ToneOfVoice.Fallback,
            Messaging = input.Profile?.Messaging ?? Domain.Branding.Messaging.Fallback,
            OperatorNotes = input.Profile?.OperatorNotes,

            // Expired and private facts are dropped here rather than filtered downstream:
            // a fact an agent can see is a fact it may cite.
            Facts = input.Facts
                .Where(f => f.IsUsableAt(asOf))
                .OrderBy(f => f.Category)
                .ThenBy(f => f.Key, StringComparer.Ordinal)
                .Select(f => new ProductFactView
                {
                    Key = f.Key,
                    Statement = f.Statement,
                    Category = f.Category.ToString(),
                    Evidence = f.Evidence,
                })
                .ToArray(),

            Personas = input.Personas
                .OrderByDescending(p => p.IsPrimary)
                .ThenBy(p => p.Name, StringComparer.Ordinal)
                .Select(p => new PersonaView
                {
                    Name = p.Name,
                    Segment = p.Segment,
                    IsPrimary = p.IsPrimary,
                    Pains = p.Detail.Pains,
                    Goals = p.Detail.Goals,
                    Objections = p.Detail.Objections,
                    Vocabulary = p.Detail.Vocabulary,
                    Context = p.Detail.Context,
                })
                .ToArray(),

            Quota = new QuotaView
            {
                Posts = input.Preferences?.PostsPerWeek ?? 2,
                Carousels = input.Preferences?.CarouselsPerWeek ?? 1,
                Reels = input.Preferences?.ReelsPerWeek ?? 1,
                ExcludedTopics = input.Preferences?.ExcludedTopics ?? [],
                PreferredTopics = input.Preferences?.PreferredTopics ?? [],
                PublishDays = input.Preferences?.PublishDays.Select(d => d.ToString()).ToArray() ?? [],
            },

            // Archived assets and derived variants are invisible to agents: the director
            // picks originals, and the renderer resolves variants itself.
            Assets = input.Assets
                .Where(a => !a.IsArchived && a.ParentAssetId is null)
                .OrderBy(a => a.Kind)
                .ThenBy(a => a.FileName, StringComparer.Ordinal)
                .Select(a => new AssetView
                {
                    Id = a.Id,
                    Kind = a.Kind.ToString(),
                    FileName = a.FileName,
                    Width = a.Width,
                    Height = a.Height,
                    Origin = a.Origin.ToString(),
                    Tags = a.Tags,
                    Description = a.Description,
                })
                .ToArray(),
        };
    }

    /// <summary>
    /// Problems that would produce bad content rather than a crash, reported before a
    /// campaign spends anything. A brand with no facts cannot make a supported claim; a
    /// brand with no screenshot cannot use half the templates.
    /// </summary>
    public static IReadOnlyList<string> Diagnose(BrandSnapshot snapshot)
    {
        var warnings = new List<string>();

        if (snapshot.Facts.Count == 0)
        {
            warnings.Add("No product facts. Every factual claim will be rejected as uncited.");
        }

        if (snapshot.Personas.Count == 0)
        {
            warnings.Add("No audience personas. The strategist has no one to write for.");
        }
        else if (!snapshot.Personas.Any(p => p.IsPrimary))
        {
            warnings.Add("No primary persona; the first one will be used by default.");
        }

        if (!snapshot.Assets.Any(a => a.Kind == nameof(AssetKind.ProductScreenshot)))
        {
            warnings.Add("No product screenshots. Templates that show the product cannot be selected.");
        }

        if (!snapshot.Assets.Any(a => a.Kind == nameof(AssetKind.Logo)))
        {
            warnings.Add("No logo. Content will render unbranded.");
        }

        if (string.IsNullOrWhiteSpace(snapshot.Messaging.Positioning))
        {
            warnings.Add("No positioning statement. Copy will drift week to week.");
        }

        warnings.AddRange(snapshot.Visual.Validate());

        return warnings;
    }
}
