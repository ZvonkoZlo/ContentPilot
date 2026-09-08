using ContentPilot.Domain.Common;

namespace ContentPilot.Domain.Branding;

/// <summary>
/// The editable working copy of a brand's identity. Everything here is consumed wholesale
/// — the renderer takes the visual identity as design tokens, the agents take the voice and
/// messaging as a prompt block — so it is stored as JSON rather than shredded into columns
/// nobody ever queries individually.
/// <para>
/// What is <em>not</em> here is as deliberate as what is: product facts, personas and
/// content preferences are relational, because they are cited by identifier, edited one at
/// a time, and counted against.
/// </para>
/// </summary>
public sealed class BrandProfile : Entity, ITenantOwned, IAuditable
{
    private BrandProfile()
    {
        Visual = null!;
        Voice = null!;
        Messaging = null!;
    }

    public BrandProfile(Guid tenantId, Guid brandId, VisualIdentity visual, ToneOfVoice voice, Messaging messaging)
    {
        TenantId = Guard.NotEmpty(tenantId);
        BrandId = Guard.NotEmpty(brandId);
        Visual = visual;
        Voice = voice;
        Messaging = messaging;
    }

    public Guid TenantId { get; private set; }

    public Guid BrandId { get; private set; }

    public VisualIdentity Visual { get; private set; }

    public ToneOfVoice Voice { get; private set; }

    public Messaging Messaging { get; private set; }

    /// <summary>Free-form notes the operator wants every agent to see. Kept short on purpose.</summary>
    public string? OperatorNotes { get; private set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }

    public void UpdateVisual(VisualIdentity visual) => Visual = visual;

    public void UpdateVoice(ToneOfVoice voice) => Voice = voice;

    public void UpdateMessaging(Messaging messaging) => Messaging = messaging;

    public void SetOperatorNotes(string? notes) =>
        OperatorNotes = string.IsNullOrWhiteSpace(notes) ? null : Guard.MaxLength(notes.Trim(), 1000);
}

/// <summary>
/// Design tokens, in the shape the renderer actually needs. Deliberately not "a design
/// system" — it is the smallest set that lets a template look like it belongs to a brand.
/// </summary>
public sealed record VisualIdentity
{
    public static readonly VisualIdentity Fallback = new()
    {
        PrimaryColor = "#3B5BDB",
        DarkColor = "#12131A",
        LightColor = "#F7F8FA",
    };

    public required string PrimaryColor { get; init; }

    public string? SecondaryColor { get; init; }

    public string? AccentColor { get; init; }

    /// <summary>Ground for light-on-dark schemes. Rarely pure black; brands have a temperature.</summary>
    public string DarkColor { get; init; } = "#12131A";

    /// <summary>Ground for dark-on-light schemes.</summary>
    public string LightColor { get; init; } = "#F7F8FA";

    /// <summary>Must be a family embedded in the renderer image, or the render is refused.</summary>
    public string HeadingFont { get; init; } = "Archivo";

    public string BodyFont { get; init; } = "Inter";

    public int CornerRadius { get; init; } = 16;

    /// <summary>
    /// Words a human would use for the look: "clean", "warm", "editorial". They steer
    /// background generation prompts and nothing else — they never reach CSS.
    /// </summary>
    public IReadOnlyList<string> StyleKeywords { get; init; } = [];

    /// <summary>
    /// Colours reach a stylesheet, so they are validated on the way in rather than trusted.
    /// Only #rgb and #rrggbb are accepted — no named colours, no functions, no expressions.
    /// </summary>
    public IEnumerable<string> Validate()
    {
        foreach (var (name, value) in new (string, string?)[]
                 {
                     (nameof(PrimaryColor), PrimaryColor),
                     (nameof(SecondaryColor), SecondaryColor),
                     (nameof(AccentColor), AccentColor),
                     (nameof(DarkColor), DarkColor),
                     (nameof(LightColor), LightColor),
                 })
        {
            if (value is not null && !IsHexColor(value))
            {
                yield return $"{name} must be a hex colour such as #6C4CF1 (was '{value}').";
            }
        }

        if (CornerRadius is < 0 or > 64)
        {
            yield return "CornerRadius must be between 0 and 64.";
        }
    }

    public static bool IsHexColor(string value)
    {
        if (value.Length is not (4 or 7) || value[0] != '#')
        {
            return false;
        }

        for (var i = 1; i < value.Length; i++)
        {
            if (!Uri.IsHexDigit(value[i]))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// How the brand sounds, and what it may never say. The prohibitions matter more than the
/// aspirations: "warm and direct" is advice, "never claim we are the cheapest" is a rule
/// the marketing QA agent can actually check.
/// </summary>
public sealed record ToneOfVoice
{
    public static readonly ToneOfVoice Fallback = new() { Summary = "Plain, warm and specific." };

    public required string Summary { get; init; }

    /// <summary>Adjectives an agent can act on: "direct", "practical", "never salesy".</summary>
    public IReadOnlyList<string> Traits { get; init; } = [];

    public IReadOnlyList<string> Avoid { get; init; } = [];

    public string? PreferredCtaStyle { get; init; }

    /// <summary>Checked literally by the copy validator, so keep them as words, not concepts.</summary>
    public IReadOnlyList<string> BannedWords { get; init; } = [];

    /// <summary>
    /// Claims the brand may never make regardless of what the facts say — legal exposure,
    /// competitor comparisons, guarantees. A hard constraint, not a preference.
    /// </summary>
    public IReadOnlyList<string> ForbiddenClaims { get; init; } = [];
}

/// <summary>Positioning and the principles that shape every piece of copy.</summary>
public sealed record Messaging
{
    public static readonly Messaging Fallback = new() { Positioning = string.Empty };

    public required string Positioning { get; init; }

    public IReadOnlyList<string> Principles { get; init; } = [];

    /// <summary>The one thing a reader should remember. Used to keep a week coherent.</summary>
    public string? CorePromise { get; init; }
}
