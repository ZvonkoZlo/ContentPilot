using ContentPilot.Application.Abstractions;
using ContentPilot.Domain.Branding;
using ContentPilot.Domain.Tenancy;
using ContentPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ContentPilot.Infrastructure.Branding;

/// <summary>
/// Seeds the Appointso brand used by development, the integration suite and the agent
/// evals.
/// <para>
/// It matters that this is <em>data</em> and not code. Nothing in the platform knows what a
/// salon is; this file happens to describe one, and a second brand in an unrelated industry
/// would be seeded the same way. The moment application code has to branch on any of this,
/// the platform has stopped being generic.
/// </para>
/// <para>
/// The facts here are the ones a copywriter may cite. They are deliberately narrow and
/// checkable — no adjectives, no claims about being best or fastest — because the marketing
/// QA agent verifies copy against them literally.
/// </para>
/// </summary>
public sealed class GoldenTenantSeeder(AppDbContext db, IMutableTenantContext tenantContext, ILogger<GoldenTenantSeeder> logger)
{
    public const string TenantSlug = "appointso";

    public async Task<SeedResult> SeedAsync(CancellationToken ct = default)
    {
        using var _ = tenantContext.BeginCrossTenantScope();

        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Slug == TenantSlug, ct);

        if (tenant is null)
        {
            tenant = new Tenant("Appointso", TenantSlug);
            db.Tenants.Add(tenant);
            await db.SaveChangesAsync(ct);
        }

        var brand = await db.Brands.FirstOrDefaultAsync(b => b.TenantId == tenant.Id, ct);

        if (brand is null)
        {
            brand = new Domain.Branding.Brand(
                tenant.Id, "Appointso", "Europe/Zagreb", ["en", "hr"], "https://appointso.com");

            db.Brands.Add(brand);
            await db.SaveChangesAsync(ct);
        }

        await SeedIndustryAsync(ct);
        await SeedProfileAsync(tenant.Id, brand.Id, ct);
        await SeedFactsAsync(tenant.Id, brand.Id, ct);
        await SeedPersonasAsync(tenant.Id, brand.Id, ct);
        await SeedPreferencesAsync(tenant.Id, brand.Id, ct);

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Golden tenant {TenantId} / brand {BrandId} seeded.", tenant.Id, brand.Id);

        return new SeedResult(tenant.Id, brand.Id);
    }

    private async Task SeedIndustryAsync(CancellationToken ct)
    {
        if (await db.IndustryProfiles.AnyAsync(i => i.Key == "beauty-salon", ct))
        {
            return;
        }

        db.IndustryProfiles.Add(new IndustryProfile("beauty-salon", "Beauty and hair salons", new IndustryDetail
        {
            SuggestedPillars = ["problem-solution", "feature-highlight", "social-proof", "how-it-works", "objection-handling"],
            TypicalPains =
            [
                "bookings arrive as DMs at all hours and get missed",
                "no-shows cost a full chair slot",
                "the owner is cutting hair and cannot answer the phone",
                "double bookings when two people manage the diary",
            ],
            Vocabulary = ["chair", "slot", "walk-in", "no-show", "regulars", "fully booked"],
            SeasonalityHints =
            [
                "December is the busiest month; do not pitch discounts",
                "January is quiet; promotions land well",
                "spring wedding season drives package bookings",
            ],
        }));
    }

    private async Task SeedProfileAsync(Guid tenantId, Guid brandId, CancellationToken ct)
    {
        if (await db.BrandProfiles.AnyAsync(p => p.BrandId == brandId, ct))
        {
            return;
        }

        db.BrandProfiles.Add(new BrandProfile(
            tenantId,
            brandId,
            new VisualIdentity
            {
                PrimaryColor = "#6C4CF1",
                SecondaryColor = "#2A2350",
                AccentColor = "#F5A8C8",
                DarkColor = "#12101F",
                LightColor = "#F7F7FB",
                HeadingFont = "Archivo",
                BodyFont = "Inter",
                CornerRadius = 16,
                StyleKeywords = ["clean", "calm", "modern", "uncluttered"],
            },
            new ToneOfVoice
            {
                Summary = "Plain and practical. Talks to a busy owner between appointments, not to a boardroom.",
                Traits = ["direct", "warm", "concrete", "never hypey"],
                Avoid = ["startup jargon", "exclamation marks", "emoji in headlines", "we are excited to announce"],
                PreferredCtaStyle = "A short imperative: Try it free, See how it works, Book a demo.",
                BannedWords = ["revolutionary", "game-changer", "seamless", "effortless", "unlock", "supercharge"],
                ForbiddenClaims =
                [
                    "any claim about being the cheapest or the best",
                    "named comparisons against competitors",
                    "guaranteed revenue or booking increases",
                    "any figure not backed by a product fact",
                ],
            },
            new Messaging
            {
                Positioning =
                    "Appointso is online booking for small salons and barbers. Clients book themselves, " +
                    "at any hour, and the diary stays correct without anyone retyping it.",
                CorePromise = "Stop losing bookings to unanswered messages.",
                Principles =
                [
                    "Lead with the owner's problem, not the feature",
                    "One idea per post",
                    "Show the real product rather than describing it",
                    "Use their words: chair, slot, no-show, regulars",
                ],
            }));
    }

    private async Task SeedFactsAsync(Guid tenantId, Guid brandId, CancellationToken ct)
    {
        var existing = await db.ProductFacts
            .Where(f => f.BrandId == brandId)
            .Select(f => f.Key)
            .ToListAsync(ct);

        (string Key, string Statement, FactCategory Category, string? Evidence)[] facts =
        [
            ("public-booking-page", "Every business gets a public booking page clients can open without an account.", FactCategory.Feature, "Booking page screenshot"),
            ("service-selection", "Clients pick the specific service they want before choosing a time.", FactCategory.Feature, "Service list screen"),
            ("employee-selection", "Clients can choose which staff member they want, or let the system assign one.", FactCategory.Feature, "Staff picker screen"),
            ("calendar-view", "Staff see the day, week and month in one calendar.", FactCategory.Feature, "Calendar screen"),
            ("appointment-management", "Appointments can be moved, cancelled or rebooked from the calendar.", FactCategory.Feature, null),
            ("automatic-reminders", "Appointso sends automatic reminders before an appointment.", FactCategory.Feature, "Reminder settings screen"),
            ("whatsapp-reminders", "Reminders can be delivered over WhatsApp.", FactCategory.Integration, "WhatsApp integration settings"),
            ("customer-records", "Every client has a record with their contact details and visit history.", FactCategory.Feature, "Customer detail screen"),
            ("booking-outside-hours", "Because the booking page is always open, clients can book outside working hours.", FactCategory.Outcome, null),
            ("no-account-needed", "Clients do not need to install an app or create an account to book.", FactCategory.Availability, null),
            ("multi-staff", "A salon with several staff members can run one shared diary.", FactCategory.Feature, null),
            ("croatian-interface", "The interface is available in Croatian and English.", FactCategory.Availability, null),
        ];

        foreach (var (key, statement, category, evidence) in facts)
        {
            if (existing.Contains(key))
            {
                continue;
            }

            var fact = new ProductFact(tenantId, brandId, key, statement, category);
            fact.SetEvidence(evidence);
            db.ProductFacts.Add(fact);
        }
    }

    private async Task SeedPersonasAsync(Guid tenantId, Guid brandId, CancellationToken ct)
    {
        if (await db.AudiencePersonas.AnyAsync(p => p.BrandId == brandId, ct))
        {
            return;
        }

        var owner = new AudiencePersona(tenantId, brandId, "Ana, salon owner", "Independent hair and beauty salons, 1-5 staff",
            new PersonaDetail
            {
                Pains =
                [
                    "booking requests arrive as Instagram DMs and get missed",
                    "she is cutting hair and cannot answer the phone",
                    "no-shows leave an empty chair she cannot refill",
                    "the paper diary is wrong the moment two people write in it",
                ],
                Goals = ["a full week", "fewer interruptions", "regulars who rebook without being chased"],
                Objections =
                [
                    "my clients are older and will not use an app",
                    "I do not have time to set anything up",
                    "another monthly subscription",
                ],
                Vocabulary = ["chair", "slot", "walk-in", "no-show", "regulars", "fully booked", "termin"],
                Context = "Checks her phone between clients. Reads captions, rarely opens links during the day.",
            });

        owner.MakePrimary();

        var barber = new AudiencePersona(tenantId, brandId, "Marko, barber shop owner", "Barber shops, walk-in heavy, 2-4 chairs",
            new PersonaDetail
            {
                Pains = ["walk-ins and appointments collide", "quiet mornings and a queue at 17:00"],
                Goals = ["spread demand across the day", "stop turning people away at peak"],
                Objections = ["we mostly take walk-ins", "the guys will not learn a new system"],
                Vocabulary = ["fade", "chair", "queue", "regulars"],
            });

        db.AudiencePersonas.AddRange(owner, barber);
    }

    private async Task SeedPreferencesAsync(Guid tenantId, Guid brandId, CancellationToken ct)
    {
        if (await db.ContentPreferences.AnyAsync(p => p.BrandId == brandId, ct))
        {
            return;
        }

        var prefs = new ContentPreferences(tenantId, brandId);
        prefs.SetQuota(posts: 2, carousels: 1, reels: 1);
        prefs.SetPreferredTopics(["no-shows", "booking outside hours", "staff scheduling", "client records"]);
        prefs.SetExcludedTopics(["politics", "discount wars", "competitor comparisons"]);
        prefs.SetPublishDays([DayOfWeek.Tuesday, DayOfWeek.Thursday, DayOfWeek.Saturday]);
        prefs.SetSchedule(DayOfWeek.Monday, new TimeOnly(6, 0), enabled: true);

        db.ContentPreferences.Add(prefs);
    }
}

public sealed record SeedResult(Guid TenantId, Guid BrandId);
