using ContentPilot.Domain.Common;

namespace ContentPilot.Domain.Branding;

/// <summary>
/// Seed material for onboarding a brand in a given industry: suggested pillars, the pains
/// people in that trade actually have, the words they use.
/// <para>
/// This is the <em>only</em> place industry knowledge is allowed to live, and it is
/// reference data rather than code. At onboarding it pre-fills a brand's profile and
/// personas, after which it is ordinary editable brand data and this row is forgotten.
/// The moment anything reads <c>if (industry == "salon")</c> in application code, the
/// design has failed and the platform has stopped being generic.
/// </para>
/// </summary>
public sealed class IndustryProfile : Entity
{
    private IndustryProfile()
    {
        Key = null!;
        Name = null!;
        Detail = null!;
    }

    public IndustryProfile(string key, string name, IndustryDetail detail)
    {
        Key = NormaliseKey(key);
        Name = Guard.MaxLength(Guard.NotBlank(name), 160);
        Detail = detail;
    }

    /// <summary>Stable slug: <c>beauty-salon</c>, <c>dental-clinic</c>.</summary>
    public string Key { get; private set; }

    public string Name { get; private set; }

    public IndustryDetail Detail { get; private set; }

    private static string NormaliseKey(string key)
    {
        var value = Guard.MaxLength(Guard.NotBlank(key), 60).ToLowerInvariant();

        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-')
            {
                throw new ArgumentException(
                    "An industry key may contain only lowercase letters, digits and hyphens.", nameof(key));
            }
        }

        return value;
    }
}

public sealed record IndustryDetail
{
    /// <summary>Content pillars that tend to work: "problem-solution", "social-proof".</summary>
    public IReadOnlyList<string> SuggestedPillars { get; init; } = [];

    public IReadOnlyList<string> TypicalPains { get; init; } = [];

    public IReadOnlyList<string> Vocabulary { get; init; } = [];

    /// <summary>
    /// When demand moves: "December is fully booked", "January is dead". The strategist
    /// uses these to avoid pitching a discount in the busiest week of the year.
    /// </summary>
    public IReadOnlyList<string> SeasonalityHints { get; init; } = [];
}
