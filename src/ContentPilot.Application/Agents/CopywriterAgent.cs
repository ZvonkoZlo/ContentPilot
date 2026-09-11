using System.Globalization;
using System.Text;
using ContentPilot.Application.Brand;

namespace ContentPilot.Application.Agents;

/// <summary>
/// Writes the words for one item, to the exact slots and budgets the chosen template
/// declared. It does not choose the template, does not decide what the week is about, and
/// does not know what the render will look like — those are Directing's, the strategist's,
/// and the renderer's jobs respectively. Each agent narrow enough to fail in one place.
/// </summary>
public sealed class CopywriterAgent : IAgent<CopywriterInput, CopySet>
{
    public string Name => "copywriter";

    public string Version => "1";

    public string PromptId => "copywriter";

    public string ResponseSchema => CopySetSchema.Json;

    public IReadOnlyDictionary<string, string> BuildVariables(CopywriterInput input) =>
        new Dictionary<string, string>
        {
            ["brand_block"] = BrandBlockRenderer.Render(
                input.Brand,
                // Neither the strategist nor the copywriter place imagery, so asset
                // identifiers are tokens spent on nothing they can act on.
                BrandBlockOptions.Default with { IncludeAssets = false }),

            ["topic"] = input.Topic,
            ["pillar"] = input.Pillar,
            ["objective"] = input.Objective,
            ["language"] = input.Language,
            ["fact_keys"] = input.AllowedFactKeys.Count > 0
                ? string.Join(", ", input.AllowedFactKeys)
                : "none — this item makes no factual claim",
            ["slots"] = FormatSlots(input.Slots),
        };

    public IReadOnlyList<string> Validate(CopySet output, CopywriterInput input) =>
        CopyValidator.Validate(output, new CopyValidator.Context
        {
            Slots = input.Slots,
            AllowedFactKeys = input.AllowedFactKeys.ToHashSet(StringComparer.Ordinal),
            BannedWords = input.Brand.Voice.BannedWords,
        });

    /// <summary>
    /// One line per slot: what it is for, and the hard ceiling it has to fit. This is the
    /// whole brief for layout — the model is never shown the template, only the numbers
    /// that matter for what it is actually asked to do.
    /// </summary>
    private static string FormatSlots(IReadOnlyList<CopySlotBrief> slots)
    {
        var builder = new StringBuilder();

        foreach (var slot in slots)
        {
            builder.Append("- ").Append(slot.Id)
                .Append(" (").Append(slot.Role).Append("): up to ")
                .Append(slot.MaxChars.ToString(CultureInfo.InvariantCulture)).Append(" characters, ")
                .Append(slot.MaxLines.ToString(CultureInfo.InvariantCulture)).Append(" line(s) max\n");
        }

        return builder.ToString().TrimEnd();
    }
}

public sealed record CopywriterInput
{
    /// <summary>The frozen snapshot — never the live profile, for the same reason the strategist gets one.</summary>
    public required BrandSnapshot Brand { get; init; }

    public required string Topic { get; init; }

    public required string Pillar { get; init; }

    /// <summary>What this item is for. The strategist's brief, read verbatim.</summary>
    public required string Objective { get; init; }

    /// <summary>The fact keys the strategist's plan allowed this item to rely on. May be empty.</summary>
    public required IReadOnlyList<string> AllowedFactKeys { get; init; }

    /// <summary>The chosen template's text slots, budgets already adjusted for <see cref="Language"/>.</summary>
    public required IReadOnlyList<CopySlotBrief> Slots { get; init; }

    public string Language { get; init; } = "en";
}
