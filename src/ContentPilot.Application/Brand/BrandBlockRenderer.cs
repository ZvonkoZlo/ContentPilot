using System.Text;

namespace ContentPilot.Application.Brand;

/// <summary>
/// Turns a snapshot into the Markdown block injected into every agent prompt.
/// <para>
/// Two properties matter and both are tested. It is <b>deterministic</b> — the same
/// snapshot produces the same bytes, so prompt caching works and two runs are comparable.
/// And it is <b>budgeted</b> — a brand block that grows without limit crowds out the
/// instructions and the content history, and the failure is silent: the model simply pays
/// less attention to the end of a long prompt.
/// </para>
/// </summary>
public static class BrandBlockRenderer
{
    /// <summary>
    /// Roughly four characters per token for English and Croatian prose. Crude, but the
    /// point is a stable ceiling, not an exact count — and it needs no tokeniser dependency.
    /// </summary>
    public const int CharactersPerToken = 4;

    public const int DefaultTokenBudget = 1200;

    public static string Render(BrandSnapshot snapshot, BrandBlockOptions? options = null)
    {
        var opts = options ?? BrandBlockOptions.Default;
        var md = new StringBuilder(4096);

        md.Append("## Brand: ").Append(snapshot.Name).Append('\n');

        if (!string.IsNullOrWhiteSpace(snapshot.Website))
        {
            md.Append("Website: ").Append(snapshot.Website).Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(snapshot.Industry))
        {
            md.Append("Industry: ").Append(snapshot.Industry).Append('\n');
        }

        md.Append("Language: ").Append(snapshot.PrimaryLanguage);

        if (snapshot.Languages.Count > 1)
        {
            md.Append(" (also ").Append(string.Join(", ", snapshot.Languages.Skip(1))).Append(')');
        }

        md.Append('\n');

        if (!string.IsNullOrWhiteSpace(snapshot.Messaging.Positioning))
        {
            md.Append("\n### Positioning\n").Append(snapshot.Messaging.Positioning).Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(snapshot.Messaging.CorePromise))
        {
            md.Append("Core promise: ").Append(snapshot.Messaging.CorePromise).Append('\n');
        }

        AppendList(md, "Messaging principles", snapshot.Messaging.Principles, opts.MaxPrinciples);

        md.Append("\n### Voice\n").Append(snapshot.Voice.Summary).Append('\n');

        AppendInline(md, "Traits", snapshot.Voice.Traits);
        AppendInline(md, "Avoid", snapshot.Voice.Avoid);

        if (!string.IsNullOrWhiteSpace(snapshot.Voice.PreferredCtaStyle))
        {
            md.Append("Preferred CTA style: ").Append(snapshot.Voice.PreferredCtaStyle).Append('\n');
        }

        // Prohibitions are never trimmed, whatever the budget. Dropping a forbidden claim to
        // save tokens is how a brand ends up publishing something legally exposed.
        AppendInline(md, "Never use these words", snapshot.Voice.BannedWords);
        AppendInline(md, "Never make these claims", snapshot.Voice.ForbiddenClaims);

        if (snapshot.Personas.Count > 0)
        {
            md.Append("\n### Audience\n");

            foreach (var persona in snapshot.Personas.Take(opts.MaxPersonas))
            {
                md.Append("- **").Append(persona.Name).Append("**");

                if (persona.IsPrimary)
                {
                    md.Append(" (primary)");
                }

                md.Append(" — ").Append(persona.Segment).Append('\n');

                AppendInline(md, "  Pains", persona.Pains.Take(opts.MaxPersonaItems));
                AppendInline(md, "  Goals", persona.Goals.Take(opts.MaxPersonaItems));
                AppendInline(md, "  Objections", persona.Objections.Take(opts.MaxPersonaItems));
                AppendInline(md, "  Their words", persona.Vocabulary.Take(opts.MaxPersonaItems));
            }
        }

        if (snapshot.Facts.Count > 0)
        {
            md.Append("\n### Product facts\n");
            md.Append("Cite a fact key for every factual claim. Claims without one are rejected.\n");

            foreach (var fact in snapshot.Facts.Take(opts.MaxFacts))
            {
                md.Append("- `").Append(fact.Key).Append("` ").Append(fact.Statement).Append('\n');
            }

            if (snapshot.Facts.Count > opts.MaxFacts)
            {
                md.Append("- (").Append(snapshot.Facts.Count - opts.MaxFacts).Append(" further facts omitted)\n");
            }
        }

        if (opts.IncludeAssets && snapshot.Assets.Count > 0)
        {
            md.Append("\n### Available assets\n");

            foreach (var asset in snapshot.Assets.Take(opts.MaxAssets))
            {
                md.Append("- `").Append(asset.Id).Append("` ").Append(asset.Kind)
                  .Append(' ').Append(asset.Width).Append('x').Append(asset.Height);

                if (!string.IsNullOrWhiteSpace(asset.Description))
                {
                    md.Append(" — ").Append(asset.Description);
                }
                else if (asset.Tags.Count > 0)
                {
                    md.Append(" — ").Append(string.Join(", ", asset.Tags));
                }

                md.Append('\n');
            }
        }

        if (!string.IsNullOrWhiteSpace(snapshot.OperatorNotes))
        {
            md.Append("\n### Operator notes\n").Append(snapshot.OperatorNotes).Append('\n');
        }

        return md.ToString();
    }

    /// <summary>Crude but stable. Used to assert the block stays inside its budget.</summary>
    public static int EstimateTokens(string block) =>
        (int)Math.Ceiling(block.Length / (double)CharactersPerToken);

    private static void AppendList(StringBuilder md, string heading, IReadOnlyList<string> values, int max)
    {
        if (values.Count == 0)
        {
            return;
        }

        md.Append('\n').Append(heading).Append(":\n");

        foreach (var value in values.Take(max))
        {
            md.Append("- ").Append(value).Append('\n');
        }
    }

    private static void AppendInline(StringBuilder md, string label, IEnumerable<string> values)
    {
        var list = values.ToArray();

        if (list.Length == 0)
        {
            return;
        }

        md.Append(label).Append(": ").Append(string.Join("; ", list)).Append('\n');
    }
}

public sealed record BrandBlockOptions
{
    public static readonly BrandBlockOptions Default = new();

    public int MaxFacts { get; init; } = 24;

    public int MaxPersonas { get; init; } = 3;

    public int MaxPersonaItems { get; init; } = 5;

    public int MaxPrinciples { get; init; } = 6;

    public int MaxAssets { get; init; } = 20;

    /// <summary>
    /// The strategist and copywriter have no use for asset identifiers; only the creative
    /// director does. Leaving them out of the other prompts saves real tokens.
    /// </summary>
    public bool IncludeAssets { get; init; } = true;

    public int TokenBudget { get; init; } = BrandBlockRenderer.DefaultTokenBudget;
}
