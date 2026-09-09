using ContentPilot.Application.Brand;
using ContentPilot.Application.ContentMemory;
using ContentPilot.Domain.Content;

namespace ContentPilot.Application.Agents;

/// <summary>
/// Checks a strategist's plan against things that can be computed.
/// <para>
/// Every rule here is one the prompt also states. That is deliberate, not redundant: the
/// prompt is how you get good output, and this is how you know you got it. A model that
/// mostly follows instructions is still a model that sometimes does not, and "mostly" is
/// not a foundation for an unattended weekly pipeline.
/// </para>
/// <para>
/// Nothing here asks a model anything. Counting items, matching strings and comparing
/// hashes are cheap, instant and never flaky.
/// </para>
/// </summary>
public static class PlanValidator
{
    public sealed record Context
    {
        public required QuotaView Quota { get; init; }

        /// <summary>Fact keys that actually exist for this brand, from the frozen snapshot.</summary>
        public required IReadOnlySet<string> KnownFactKeys { get; init; }

        /// <summary>Topic hashes from recent approved content.</summary>
        public required IReadOnlyList<long> RecentTopicHashes { get; init; }

        /// <summary>Empty means the strategist may use any day.</summary>
        public IReadOnlySet<DayOfWeek> AllowedDays { get; init; } = new HashSet<DayOfWeek>();
    }

    public static IReadOnlyList<string> Validate(WeeklyPlan plan, Context context)
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(plan.Theme))
        {
            problems.Add("The plan has no theme; a week without one is a bag of posts.");
        }

        ValidateQuota(plan, context, problems);
        ValidateItems(plan, context, problems);
        ValidateNovelty(plan, context, problems);

        return problems;
    }

    /// <summary>
    /// Exact counts, not approximate. A plan with the wrong number of items is rejected
    /// whole rather than trimmed: silently dropping the fourth item would mean the operator
    /// gets three posts and no explanation.
    /// </summary>
    private static void ValidateQuota(WeeklyPlan plan, Context context, List<string> problems)
    {
        (string Name, int Expected)[] expected =
        [
            (nameof(ContentItemType.StaticPost), context.Quota.Posts),
            (nameof(ContentItemType.Carousel), context.Quota.Carousels),
            (nameof(ContentItemType.Reel), context.Quota.Reels),
        ];

        foreach (var (name, want) in expected)
        {
            var got = plan.Items.Count(item => string.Equals(item.Type, name, StringComparison.OrdinalIgnoreCase));

            if (got != want)
            {
                problems.Add($"Quota: expected {want} {name} item(s), got {got}.");
            }
        }

        var unknown = plan.Items
            .Where(item => !Enum.TryParse<ContentItemType>(item.Type, ignoreCase: true, out _))
            .Select(item => item.Type)
            .Distinct()
            .ToArray();

        if (unknown.Length > 0)
        {
            problems.Add($"Unknown item type(s): {string.Join(", ", unknown)}.");
        }
    }

    private static void ValidateItems(WeeklyPlan plan, Context context, List<string> problems)
    {
        for (var i = 0; i < plan.Items.Count; i++)
        {
            var item = plan.Items[i];
            var label = $"Item {i + 1} ('{Truncate(item.Topic)}')";

            if (string.IsNullOrWhiteSpace(item.Topic))
            {
                problems.Add($"Item {i + 1} has no topic.");
            }

            if (string.IsNullOrWhiteSpace(item.Objective))
            {
                problems.Add($"{label} has no objective; the copywriter reads it as its brief.");
            }

            // Excluded topics are the operator's judgement about their own business, so the
            // match is substring and case-insensitive rather than clever.
            var haystack = $"{item.Topic} {item.Objective} {item.Pillar}";

            foreach (var banned in context.Quota.ExcludedTopics
                .Where(b => haystack.Contains(b, StringComparison.OrdinalIgnoreCase)))
            {
                problems.Add($"{label} touches the excluded topic '{banned}'.");
            }

            // A key that does not resolve is a fabricated citation. Rejecting the plan is
            // cheaper than letting the copywriter build a claim on it and catching it later.
            foreach (var key in item.FactKeys.Where(k => !context.KnownFactKeys.Contains(k)))
            {
                problems.Add($"{label} cites fact key '{key}', which this brand does not have.");
            }

            if (!Enum.TryParse<DayOfWeek>(item.PublishDay, ignoreCase: true, out var day))
            {
                problems.Add($"{label} has an unrecognised publish day '{item.PublishDay}'.");
            }
            else if (context.AllowedDays.Count > 0 && !context.AllowedDays.Contains(day))
            {
                problems.Add(
                    $"{label} publishes on {day}, which the brand does not publish on " +
                    $"({string.Join(", ", context.AllowedDays.Order())}).");
            }
        }
    }

    /// <summary>
    /// Two passes: against history, and against the plan itself. The second matters more
    /// often than you would expect — a model asked for five items will sometimes produce
    /// the same idea twice with different wording.
    /// </summary>
    private static void ValidateNovelty(WeeklyPlan plan, Context context, List<string> problems)
    {
        var hashes = plan.Items.Select(item => SimHash.Compute(item.Topic)).ToArray();

        for (var i = 0; i < plan.Items.Count; i++)
        {
            foreach (var recent in context.RecentTopicHashes)
            {
                if (SimHash.IsNearDuplicate(hashes[i], recent))
                {
                    problems.Add(
                        $"Item {i + 1} ('{Truncate(plan.Items[i].Topic)}') repeats recent content " +
                        $"(distance {SimHash.Distance(hashes[i], recent)}).");
                    break;
                }
            }

            for (var j = i + 1; j < plan.Items.Count; j++)
            {
                if (SimHash.IsNearDuplicate(hashes[i], hashes[j]))
                {
                    problems.Add(
                        $"Items {i + 1} and {j + 1} are the same idea in different words " +
                        $"(distance {SimHash.Distance(hashes[i], hashes[j])}).");
                }
            }
        }
    }

    private static string Truncate(string text) =>
        text.Length <= 40 ? text : text[..37] + "...";
}
