using ContentPilot.Application.Brand;
using ContentPilot.Domain.Content;
using ContentPilot.Rendering.Contracts;

namespace ContentPilot.Application.Agents;

/// <summary>
/// Works out which templates could render this item at all.
/// <para>
/// Eligibility is a computation; ranking is a judgement. Splitting them is what stops the
/// creative director from picking a template that needs a product screenshot the brand has
/// never uploaded — not by asking it nicely, but by never showing it that option. The model
/// then chooses between candidates that are all renderable, which is a far easier question
/// and a far cheaper failure when it gets it wrong.
/// </para>
/// </summary>
public static class TemplateSelector
{
    public sealed record Candidate
    {
        public required TemplateManifest Template { get; init; }

        public required AspectRatio Ratio { get; init; }

        /// <summary>Asset slot id to the asset that would fill it.</summary>
        public required IReadOnlyDictionary<string, AssetView> AssetAssignments { get; init; }

        /// <summary>
        /// Ordering hint only — a tie-break before the model sees the list, never a reason
        /// to exclude anything. Real ranking is the director's job.
        /// </summary>
        public required int PillarAffinity { get; init; }

        public string Reference => $"{Template.TemplateId}@v{Template.Version}";
    }

    public sealed record Rejection(string TemplateId, string Reason);

    public sealed record Result(IReadOnlyList<Candidate> Candidates, IReadOnlyList<Rejection> Rejections);

    /// <summary>
    /// Every template is either a candidate or a rejection with a reason. Nothing is dropped
    /// silently: "no template was eligible" is a question an operator will ask, and the
    /// answer has to be better than a shrug.
    /// </summary>
    public static Result Select(
        IReadOnlyList<TemplateManifest> templates,
        ContentItemType itemType,
        string pillar,
        BrandSnapshot brand,
        AspectRatio? preferredRatio = null)
    {
        var candidates = new List<Candidate>();
        var rejections = new List<Rejection>();
        var contentType = ToContentType(itemType);

        var assetsByKind = brand.Assets
            .GroupBy(asset => asset.Kind, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        foreach (var template in templates)
        {
            if (!template.ContentTypes.Contains(contentType))
            {
                rejections.Add(new Rejection(template.TemplateId, $"Does not support {contentType}."));
                continue;
            }

            var ratio = preferredRatio is { } wanted && template.Supports(wanted)
                ? wanted
                : template.AspectRatios.FirstOrDefault();

            if (preferredRatio is { } required && !template.Supports(required))
            {
                rejections.Add(new Rejection(template.TemplateId, $"Does not support {required}."));
                continue;
            }

            var assignments = new Dictionary<string, AssetView>(StringComparer.Ordinal);
            string? missing = null;

            foreach (var slot in template.AssetSlots.Where(slot => slot.Required))
            {
                var asset = PickAsset(slot, assetsByKind);

                if (asset is null)
                {
                    missing = slot.Id;
                    break;
                }

                assignments[slot.Id] = asset;
            }

            if (missing is not null)
            {
                rejections.Add(new Rejection(
                    template.TemplateId,
                    $"Needs an asset for '{missing}' and the library has none of the right kind."));

                continue;
            }

            // Optional slots are filled when something suits, and left alone otherwise.
            foreach (var slot in template.AssetSlots.Where(slot => !slot.Required))
            {
                if (PickAsset(slot, assetsByKind) is { } optional)
                {
                    assignments[slot.Id] = optional;
                }
            }

            candidates.Add(new Candidate
            {
                Template = template,
                Ratio = ratio,
                AssetAssignments = assignments,
                PillarAffinity = template.PillarAffinity.Contains(pillar, StringComparer.OrdinalIgnoreCase) ? 1 : 0,
            });
        }

        // Safe mode last. It is the escalation ladder's final rung, not a first choice, and
        // putting it at the top of the list would quietly make every week look the same.
        var ordered = candidates
            .OrderBy(candidate => candidate.Template.SafeMode)
            .ThenByDescending(candidate => candidate.PillarAffinity)
            .ThenBy(candidate => candidate.Template.TemplateId, StringComparer.Ordinal)
            .ToArray();

        return new Result(ordered, rejections);
    }

    /// <summary>
    /// Immutable slots take uploads only. A generated image in a slot that promises to show
    /// the real product is the one failure this system must never have, so it is prevented
    /// by construction rather than checked for afterwards.
    /// </summary>
    private static AssetView? PickAsset(
        AssetSlot slot,
        IReadOnlyDictionary<string, AssetView[]> assetsByKind)
    {
        if (!assetsByKind.TryGetValue(slot.Kind.ToString(), out var available))
        {
            return null;
        }

        var usable = slot.Immutable
            ? available.Where(asset => asset.Origin == nameof(Domain.Branding.AssetOrigin.Upload))
            : available;

        // Widest first: a screenshot scaled down keeps its detail, one scaled up does not.
        return usable
            .OrderByDescending(asset => (long)asset.Width * asset.Height)
            .FirstOrDefault();
    }

    /// <summary>
    /// A deliverable is a post; a template renders one frame of one. The names differ
    /// because a carousel is several slides from one template, and a reel several scenes.
    /// </summary>
    private static TemplateContentType ToContentType(ContentItemType type) => type switch
    {
        ContentItemType.StaticPost => TemplateContentType.Static,
        ContentItemType.Carousel => TemplateContentType.CarouselSlide,
        ContentItemType.Reel => TemplateContentType.ReelScene,
        _ => TemplateContentType.Static,
    };
}
