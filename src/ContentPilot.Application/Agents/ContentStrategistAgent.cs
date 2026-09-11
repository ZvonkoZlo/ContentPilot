using System.Globalization;
using System.Text;
using ContentPilot.Application.Brand;

namespace ContentPilot.Application.Agents;

/// <summary>
/// Decides what a brand should say this week, and nothing more.
/// <para>
/// It does not write captions or choose templates. Letting one agent do everything is how
/// you get a week that is plausible item by item and incoherent as a whole — the model
/// optimises each piece in isolation and never steps back to look at the seven days.
/// </para>
/// </summary>
public sealed class ContentStrategistAgent : IAgent<StrategistInput, WeeklyPlan>
{
    public string Name => "content-strategist";

    /// <summary>Bumped when the input or output shape changes, not when the prompt is edited.</summary>
    public string Version => "1";

    public string PromptId => "content-strategist";

    public string ResponseSchema => WeeklyPlanSchema.Json;

    public IReadOnlyDictionary<string, string> BuildVariables(StrategistInput input) =>
        new Dictionary<string, string>
        {
            ["brand_block"] = BrandBlockRenderer.Render(
                input.Brand,
                // The strategist chooses subjects, not pictures. Asset identifiers would be
                // several hundred tokens it has no use for.
                BrandBlockOptions.Default with { IncludeAssets = false }),

            ["recent_content"] = FormatRecentContent(input.RecentContent),
            ["week_start"] = input.WeekStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["posts"] = input.Brand.Quota.Posts.ToString(CultureInfo.InvariantCulture),
            ["carousels"] = input.Brand.Quota.Carousels.ToString(CultureInfo.InvariantCulture),
            ["reels"] = input.Brand.Quota.Reels.ToString(CultureInfo.InvariantCulture),
            ["publish_days"] = input.Brand.Quota.PublishDays.Count > 0
                ? string.Join(", ", input.Brand.Quota.PublishDays)
                : "any day",
            ["extra_direction"] = string.IsNullOrWhiteSpace(input.ExtraDirection)
                ? string.Empty
                : $"The operator has asked for this specifically: {input.ExtraDirection.Trim()}",
        };

    public IReadOnlyList<string> Validate(WeeklyPlan output, StrategistInput input) =>
        PlanValidator.Validate(output, new PlanValidator.Context
        {
            Quota = input.Brand.Quota,
            KnownFactKeys = input.Brand.Facts.Select(fact => fact.Key).ToHashSet(StringComparer.Ordinal),
            RecentTopicHashes = input.RecentContent.Select(entry => entry.TopicHash).ToArray(),
            AllowedDays = input.Brand.Quota.PublishDays
                .Select(day => Enum.Parse<DayOfWeek>(day, ignoreCase: true))
                .ToHashSet(),
        });

    /// <summary>
    /// How many past items the model is shown. The novelty check carries a far longer tail
    /// of hashes, because those are eight bytes each and cost nothing; prose in a prompt is
    /// not, and every token spent on history is one not spent on the brief.
    /// </summary>
    public const int RecentContentShown = 12;

    /// <summary>
    /// A compact list, not full posts. The strategist needs to know what ground is already
    /// covered, not to re-read a month of captions.
    /// <para>
    /// Entries whose text was dropped for the novelty tail are skipped rather than printed
    /// as blanks — the caller may legitimately pass more than is meant to be shown.
    /// </para>
    /// </summary>
    private static string FormatRecentContent(IReadOnlyList<RecentContent> recent)
    {
        var shown = recent
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Topic))
            .Take(RecentContentShown)
            .ToArray();

        if (shown.Length == 0)
        {
            return "Nothing has been published yet. This is the first week.";
        }

        var builder = new StringBuilder();

        foreach (var entry in shown)
        {
            builder.Append("- ")
                .Append(entry.ApprovedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .Append(" · ").Append(entry.Pillar)
                .Append(" · ").Append(entry.Topic);

            // The operator's own rating is the only signal that says whether any of this is
            // working, so it goes in front of the model when it exists.
            if (entry.HumanRating is { } rating)
            {
                builder.Append(" (rated ").Append(rating).Append("/5)");
            }

            builder.Append('\n');
        }

        return builder.ToString().TrimEnd();
    }
}

public sealed record StrategistInput
{
    /// <summary>The frozen snapshot. Never the live profile, or a mid-week edit would change it.</summary>
    public required BrandSnapshot Brand { get; init; }

    /// <summary>Newest first. Both context for the model and input to the novelty check.</summary>
    public required IReadOnlyList<RecentContent> RecentContent { get; init; }

    public required DateOnly WeekStart { get; init; }

    /// <summary>A one-off steer from the operator: a launch, a promotion, a season.</summary>
    public string? ExtraDirection { get; init; }
}

public sealed record RecentContent
{
    public required string Topic { get; init; }

    public required string Pillar { get; init; }

    /// <summary>Precomputed, so the validator never re-tokenises a month of history.</summary>
    public required long TopicHash { get; init; }

    public required DateTimeOffset ApprovedAt { get; init; }

    public int? HumanRating { get; init; }
}
