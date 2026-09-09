using System.Reflection;
using System.Text;

namespace ContentPilot.Application.Prompts;

/// <summary>
/// Loads prompts embedded in the assembly and hands them out by id.
/// <para>
/// Embedded rather than read from disk so a deployed worker cannot disagree with the API
/// about what a prompt says, and so the text is present in exactly the build that produced
/// a given output.
/// </para>
/// </summary>
public sealed class PromptLibrary
{
    private readonly Dictionary<string, PromptTemplate> _prompts;

    private PromptLibrary(Dictionary<string, PromptTemplate> prompts) => _prompts = prompts;

    public IReadOnlyCollection<PromptTemplate> All => _prompts.Values;

    public static PromptLibrary LoadEmbedded(Assembly? assembly = null)
    {
        assembly ??= typeof(PromptLibrary).Assembly;

        var prompts = new Dictionary<string, PromptTemplate>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".prompt.md", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var template = Parse(reader.ReadToEnd(), name);

            if (!prompts.TryAdd(template.PromptId, template))
            {
                throw new InvalidOperationException(
                    $"Two prompts claim the id '{template.PromptId}'. Ids are how runs are traced back to text.");
            }
        }

        if (prompts.Count == 0)
        {
            throw new InvalidOperationException(
                "No prompts were embedded. Check that *.prompt.md files are marked as EmbeddedResource.");
        }

        return new PromptLibrary(prompts);
    }

    public PromptTemplate Get(string promptId) =>
        _prompts.TryGetValue(promptId, out var prompt)
            ? prompt
            : throw new InvalidOperationException(
                $"No prompt '{promptId}'. Available: {string.Join(", ", _prompts.Keys.Order())}.");

    /// <summary>
    /// Format:
    /// <code>
    /// ---
    /// id: content-strategist
    /// version: 1
    /// profile: strategist
    /// schema: WeeklyPlan
    /// ---
    /// ## system
    /// ...
    /// ## user
    /// ...
    /// </code>
    /// </summary>
    private static PromptTemplate Parse(string content, string resourceName)
    {
        var normalised = content.Replace("\r\n", "\n");

        if (!normalised.StartsWith("---\n", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{resourceName} does not begin with front matter.");
        }

        var end = normalised.IndexOf("\n---\n", 4, StringComparison.Ordinal);

        if (end < 0)
        {
            throw new InvalidOperationException($"{resourceName} has unterminated front matter.");
        }

        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in normalised[4..end].Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(':', StringComparison.Ordinal);

            if (separator > 0)
            {
                meta[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }
        }

        var body = normalised[(end + 5)..];
        var userMarker = body.IndexOf("\n## user\n", StringComparison.Ordinal);

        if (!body.StartsWith("## system\n", StringComparison.Ordinal) || userMarker < 0)
        {
            throw new InvalidOperationException(
                $"{resourceName} must contain a '## system' section followed by a '## user' section.");
        }

        string Required(string key) => meta.TryGetValue(key, out var value) && value.Length > 0
            ? value
            : throw new InvalidOperationException($"{resourceName} is missing '{key}' in its front matter.");

        return new PromptTemplate
        {
            PromptId = Required("id"),
            Version = int.Parse(Required("version")),
            ModelProfile = Required("profile"),
            SchemaName = meta.GetValueOrDefault("schema"),
            System = body["## system\n".Length..userMarker].Trim(),
            User = body[(userMarker + "\n## user\n".Length)..].Trim(),
        };
    }
}
