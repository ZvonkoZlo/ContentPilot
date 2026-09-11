using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ContentPilot.Application.Prompts;

/// <summary>
/// A prompt as it lives on disk: front matter naming the model profile and schema, then the
/// body.
/// <para>
/// Prompts are files rather than string literals so that changing one is a reviewable diff,
/// and so the exact text behind any past output can be recovered from its hash.
/// </para>
/// </summary>
public sealed record PromptTemplate
{
    private static readonly Regex Placeholder = new(@"\{\{\s*([a-zA-Z_][a-zA-Z0-9_]*)\s*\}\}", RegexOptions.Compiled);

    public required string PromptId { get; init; }

    public required int Version { get; init; }

    public required string ModelProfile { get; init; }

    public string? SchemaName { get; init; }

    /// <summary>Stable across a campaign, so it is the half worth caching.</summary>
    public required string System { get; init; }

    /// <summary>Carries the per-call variables. Never cached.</summary>
    public required string User { get; init; }

    public string ContentHash =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{System}\n---\n{User}")));

    public string Reference => $"{PromptId}@v{Version}";

    /// <summary>
    /// Substitutes <c>{{name}}</c> placeholders.
    /// <para>
    /// Strict in both directions on purpose. A variable the template does not use is
    /// usually a rename that was half applied; a placeholder with nothing to fill it would
    /// otherwise reach the model as the literal text <c>{{brand_block}}</c>, which no model
    /// will complain about and every reader will misread as working.
    /// </para>
    /// </summary>
    public (string System, string User) Render(IReadOnlyDictionary<string, string> variables)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var missing = new List<string>();

        string Substitute(string text) => Placeholder.Replace(text, match =>
        {
            var name = match.Groups[1].Value;

            if (!variables.TryGetValue(name, out var value))
            {
                missing.Add(name);
                return match.Value;
            }

            used.Add(name);
            return value;
        });

        var system = Substitute(System);
        var user = Substitute(User);

        if (missing.Count > 0)
        {
            throw new PromptRenderException(
                $"{Reference} has no value for: {string.Join(", ", missing.Distinct().Order())}.");
        }

        var unused = variables.Keys.Where(key => !used.Contains(key)).Order().ToArray();

        if (unused.Length > 0)
        {
            throw new PromptRenderException(
                $"{Reference} was given variables it does not use: {string.Join(", ", unused)}. " +
                "This is usually a rename applied on one side only.");
        }

        return (system, user);
    }

    /// <summary>Every placeholder the template expects, for validation at startup.</summary>
    public IReadOnlyCollection<string> RequiredVariables() =>
        Placeholder.Matches($"{System}\n{User}")
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .Order()
            .ToArray();
}

public sealed class PromptRenderException(string message) : Exception(message);
