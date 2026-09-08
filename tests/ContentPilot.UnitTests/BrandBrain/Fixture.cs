using ContentPilot.Application.Brand;
using ContentPilot.Domain.Branding;

namespace ContentPilot.UnitTests.BrandBrain;

/// <summary>
/// A small but complete brand. Complete matters: several tests assert that a well-filled
/// brain raises no warnings, which only means something if the fixture really has
/// everything the diagnostics look for.
/// </summary>
internal static class Fixture
{
    public static readonly Guid TenantId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    public static readonly Guid BrandId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    // Built once and reused. Entities mint a fresh UUIDv7 in their constructor, so calling
    // the factories twice would produce brands and assets with different identifiers — and
    // the determinism tests would be asserting that two different brains hash the same,
    // which is not a property anyone wants.
    private static readonly global::ContentPilot.Domain.Branding.Brand SharedBrand = Brand();
    private static readonly BrandProfile SharedProfile = Profile();
    private static readonly List<ProductFact> SharedFacts = Facts();
    private static readonly List<AudiencePersona> SharedPersonas = Personas();
    private static readonly ContentPreferences SharedPreferences = Preferences();
    private static readonly List<BrandAsset> SharedAssets = Assets();

    public static BrandBrainAssembler.Input Input() => new(
        SharedBrand,
        SharedProfile,
        SharedFacts,
        SharedPersonas,
        SharedPreferences,
        SharedAssets,
        "beauty-salon");

    public static global::ContentPilot.Domain.Branding.Brand Brand() =>
        new(TenantId, "Appointso", "Europe/Zagreb", ["en", "hr"], "https://appointso.com");

    public static BrandProfile Profile() => new(
        TenantId,
        BrandId,
        new VisualIdentity
        {
            PrimaryColor = "#6C4CF1",
            AccentColor = "#F5A8C8",
            StyleKeywords = ["clean", "calm"],
        },
        new ToneOfVoice
        {
            Summary = "Plain and practical.",
            Traits = ["direct", "warm"],
            BannedWords = ["revolutionary", "seamless"],
            ForbiddenClaims = ["any claim about being the cheapest"],
        },
        new Messaging
        {
            Positioning = "Online booking for small salons.",
            CorePromise = "Stop losing bookings to unanswered messages.",
            Principles = ["Lead with the problem", "One idea per post"],
        });

    public static List<ProductFact> Facts() =>
    [
        Fact("public-booking-page", "Every business gets a public booking page.", FactCategory.Feature),
        Fact("whatsapp-reminders", "Reminders can be delivered over WhatsApp.", FactCategory.Integration),
        Fact("no-account-needed", "Clients do not need an account to book.", FactCategory.Availability),
    ];

    public static ProductFact Fact(string key, string statement, FactCategory category) =>
        new(TenantId, BrandId, key, statement, category);

    public static List<AudiencePersona> Personas()
    {
        var owner = new AudiencePersona(TenantId, BrandId, "Ana, salon owner", "Independent salons, 1-5 staff",
            new PersonaDetail
            {
                Pains = ["booking requests arrive as DMs and get missed"],
                Goals = ["a full week"],
                Objections = ["my clients are older"],
                Vocabulary = ["chair", "slot", "no-show"],
            });

        owner.MakePrimary();

        var barber = new AudiencePersona(TenantId, BrandId, "Marko, barber", "Barber shops",
            new PersonaDetail { Pains = ["walk-ins collide with appointments"] });

        return [owner, barber];
    }

    public static ContentPreferences Preferences()
    {
        var prefs = new ContentPreferences(TenantId, BrandId);
        prefs.SetQuota(2, 1, 1);
        prefs.SetExcludedTopics(["politics"]);

        return prefs;
    }

    public static List<BrandAsset> Assets() =>
    [
        Asset("booking-screen.png", AssetKind.ProductScreenshot),
        Asset("calendar.png", AssetKind.ProductScreenshot),
        Asset("logo.png", AssetKind.Logo),
    ];

    public static BrandAsset Asset(string fileName, AssetKind kind) =>
        new(TenantId, BrandId, kind, fileName, $"t/x/{fileName}", "image/png",
            $"sha-{fileName}", 12345, 828, 1792, AssetOrigin.Upload, ["ui"]);
}
