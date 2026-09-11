using System.Text.RegularExpressions;

namespace ContentPilot.Application.Agents;

/// <summary>
/// Checks a copywriter's output against things that can be computed — the same split
/// <see cref="PlanValidator"/> makes, and for the same reason: the prompt states every one
/// of these rules, and this is how the pipeline knows it was actually followed rather than
/// mostly followed.
/// </summary>
public static partial class CopyValidator
{
    public sealed record Context
    {
        /// <summary>The slots the chosen template declared, with the budgets copy must fit.</summary>
        public required IReadOnlyList<CopySlotBrief> Slots { get; init; }

        /// <summary>Fact keys the plan's item was allowed to cite. From the frozen snapshot.</summary>
        public required IReadOnlySet<string> AllowedFactKeys { get; init; }

        /// <summary>Words the brand voice forbids outright, checked literally.</summary>
        public IReadOnlyList<string> BannedWords { get; init; } = [];
    }

    public static IReadOnlyList<string> Validate(CopySet copy, Context context)
    {
        var problems = new List<string>();

        ValidateSlotCoverage(copy, context, problems);
        ValidateBudgets(copy, context, problems);
        ValidateFactCitations(copy, context, problems);
        ValidateBannedWords(copy, problems, context);

        return problems;
    }

    /// <summary>
    /// Every slot the brief asked for must appear exactly once, and nothing else may. A
    /// missing slot renders as a hole in the template; an extra one is copy for nowhere.
    /// </summary>
    private static void ValidateSlotCoverage(CopySet copy, Context context, List<string> problems)
    {
        var expected = context.Slots.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var slot in copy.Slots)
        {
            if (!expected.Contains(slot.Id))
            {
                problems.Add($"Copy was written for slot '{slot.Id}', which this template does not have.");
                continue;
            }

            if (!seen.Add(slot.Id))
            {
                problems.Add($"Slot '{slot.Id}' was written twice.");
            }

            if (string.IsNullOrWhiteSpace(slot.Text))
            {
                problems.Add($"Slot '{slot.Id}' has no text.");
            }
        }

        foreach (var missing in expected.Except(seen))
        {
            problems.Add($"Slot '{missing}' has no copy.");
        }
    }

    /// <summary>
    /// A hard ceiling, checked in characters exactly as briefed — the brief already applied
    /// <c>TextSlot.BudgetFor</c>'s language adjustment, so this side does no arithmetic of
    /// its own, only a comparison.
    /// </summary>
    private static void ValidateBudgets(CopySet copy, Context context, List<string> problems)
    {
        var budgets = context.Slots.ToDictionary(s => s.Id, s => s.MaxChars, StringComparer.Ordinal);

        foreach (var slot in copy.Slots)
        {
            if (budgets.TryGetValue(slot.Id, out var max) && slot.Text.Length > max)
            {
                problems.Add(
                    $"Slot '{slot.Id}' is {slot.Text.Length} characters against a budget of {max}.");
            }
        }
    }

    /// <summary>
    /// A key that does not resolve is a fabricated citation — rejecting the copy is cheaper
    /// than letting a claim reach MarketingQA and catching it there instead.
    /// </summary>
    private static void ValidateFactCitations(CopySet copy, Context context, List<string> problems)
    {
        foreach (var key in copy.FactCitations.Where(k => !context.AllowedFactKeys.Contains(k)))
        {
            problems.Add($"Copy cites fact key '{key}', which this item's brief did not list.");
        }
    }

    /// <summary>
    /// Literal, not conceptual, and word-bounded so "class" does not trip a ban on "ass".
    /// The brand voice states these in the prompt too; this is what confirms they were
    /// actually honoured rather than merely requested.
    /// </summary>
    private static void ValidateBannedWords(CopySet copy, List<string> problems, Context context)
    {
        if (context.BannedWords.Count == 0)
        {
            return;
        }

        var combined = string.Join(" ", copy.Slots.Select(s => s.Text));

        foreach (var banned in context.BannedWords)
        {
            if (string.IsNullOrWhiteSpace(banned))
            {
                continue;
            }

            if (WordBoundary(Regex.Escape(banned.Trim())).IsMatch(combined))
            {
                problems.Add($"Copy uses the word '{banned}', which this brand's voice forbids.");
            }
        }
    }

    private static Regex WordBoundary(string escapedWord) =>
        new($@"(?<![\p{{L}}\p{{N}}])({escapedWord})(?![\p{{L}}\p{{N}}])", RegexOptions.IgnoreCase);
}

/// <summary>One slot's budget, as the chosen template's manifest declared it for this language.</summary>
public sealed record CopySlotBrief
{
    public required string Id { get; init; }

    public required string Role { get; init; }

    public required int MaxChars { get; init; }

    public required int MaxLines { get; init; }
}
