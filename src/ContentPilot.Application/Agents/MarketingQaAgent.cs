using System.Text;
using ContentPilot.Application.Brand;

namespace ContentPilot.Application.Agents;

/// <summary>
/// Gate 3. Text only, so it is cheap, and runs alongside gate 2 rather than after it. Its
/// most important job is claim grounding: every factual assertion in the copy must cite a
/// <c>ProductFact</c>, and this agent verifies the citation actually supports the claim —
/// the one check <see cref="CopyValidator"/> could not make at Writing time, because knowing
/// a cited key exists is not the same as knowing the sentence built on it is true to what
/// that fact actually says.
/// </summary>
public sealed class MarketingQaAgent : IAgent<MarketingQaInput, MarketingQaOutput>
{
    public string Name => "marketing-qa";

    public string Version => "1";

    public string PromptId => "marketing-qa";

    public string ResponseSchema => MarketingQaOutputSchema.Json;

    public IReadOnlyDictionary<string, string> BuildVariables(MarketingQaInput input) =>
        new Dictionary<string, string>
        {
            ["brand_block"] = BrandBlockRenderer.Render(input.Brand, BrandBlockOptions.Default with { IncludeAssets = false }),
            ["topic"] = input.Topic,
            ["objective"] = input.Objective,
            ["copy"] = FormatCopy(input.Copy),
            ["cited_facts"] = FormatCitedFacts(input.Copy, input.Brand),
            ["recent_hooks"] = FormatRecentHooks(input.RecentContent),
        };

    public IReadOnlyList<string> Validate(MarketingQaOutput output, MarketingQaInput input) =>
        MarketingQaValidator.Validate(output);

    private static string FormatCopy(CopySet copy)
    {
        var builder = new StringBuilder();

        foreach (var slot in copy.Slots)
        {
            builder.Append("- ").Append(slot.Id).Append(": \"").Append(slot.Text).Append("\"\n");
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Only the facts the copy actually cited, with their statement text — this is what the
    /// model checks the copy's claims against, not the whole fact library.
    /// </summary>
    private static string FormatCitedFacts(CopySet copy, BrandSnapshot brand)
    {
        if (copy.FactCitations.Count == 0)
        {
            return "None cited. This copy makes no factual claim that needs grounding.";
        }

        var byKey = brand.Facts.ToDictionary(f => f.Key, StringComparer.Ordinal);
        var builder = new StringBuilder();

        foreach (var key in copy.FactCitations)
        {
            var statement = byKey.TryGetValue(key, out var fact) ? fact.Statement : "(unknown fact key — this citation cannot be verified)";
            builder.Append("- `").Append(key).Append("`: ").Append(statement).Append('\n');
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatRecentHooks(IReadOnlyList<RecentContent> recent)
    {
        var shown = recent.Where(r => !string.IsNullOrWhiteSpace(r.Topic)).Take(12).ToArray();

        if (shown.Length == 0)
        {
            return "Nothing published yet.";
        }

        var builder = new StringBuilder();

        foreach (var entry in shown)
        {
            builder.Append("- ").Append(entry.Topic).Append('\n');
        }

        return builder.ToString().TrimEnd();
    }
}

public sealed record MarketingQaInput
{
    public required BrandSnapshot Brand { get; init; }

    public required string Topic { get; init; }

    public required string Objective { get; init; }

    public required CopySet Copy { get; init; }

    /// <summary>The same recent-content window the strategist reads, for the repetition check.</summary>
    public IReadOnlyList<RecentContent> RecentContent { get; init; } = [];
}
