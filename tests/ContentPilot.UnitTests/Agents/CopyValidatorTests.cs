using ContentPilot.Application.Agents;
using Shouldly;

namespace ContentPilot.UnitTests.Agents;

/// <summary>
/// The deterministic half of the copy contract, exercised directly against the validator
/// rather than through the agent — every rule the copywriter prompt states, confirmed
/// rather than trusted.
/// </summary>
public sealed class CopyValidatorTests
{
    private static readonly CopySlotBrief[] Slots =
    [
        new() { Id = "headline", Role = "headline", MaxChars = 20, MaxLines = 2 },
        new() { Id = "body", Role = "body", MaxChars = 40, MaxLines = 3 },
    ];

    private static CopyValidator.Context Context(
        IEnumerable<string>? allowedFactKeys = null,
        IEnumerable<string>? bannedWords = null,
        CopySlotBrief[]? slots = null) => new()
    {
        Slots = slots ?? Slots,
        AllowedFactKeys = (allowedFactKeys ?? []).ToHashSet(),
        BannedWords = (bannedWords ?? []).ToArray(),
    };

    private static CopySet Copy(params (string Id, string Text)[] slots) => new()
    {
        Slots = [.. slots.Select(s => new CopySlot { Id = s.Id, Text = s.Text })],
        FactCitations = [],
    };

    [Fact]
    public void Complete_copy_within_every_budget_is_clean()
    {
        var copy = Copy(("headline", "Fill the slot"), ("body", "Clients book themselves, day or night."));

        CopyValidator.Validate(copy, Context()).ShouldBeEmpty();
    }

    [Fact]
    public void A_slot_over_its_character_budget_is_rejected()
    {
        var copy = Copy(("headline", "This headline is far too long for the budget"), ("body", "fine"));

        var problems = CopyValidator.Validate(copy, Context());

        problems.ShouldContain(p => p.Contains("headline") && p.Contains("20"));
    }

    [Fact]
    public void A_slot_landing_exactly_on_the_budget_is_not_rejected()
    {
        var copy = Copy(("headline", new string('a', 20)), ("body", "fine"));

        CopyValidator.Validate(copy, Context()).ShouldBeEmpty();
    }

    [Fact]
    public void A_missing_slot_is_reported_by_id()
    {
        var copy = Copy(("headline", "Fill the slot"));

        CopyValidator.Validate(copy, Context()).ShouldContain(p => p.Contains("body") && p.Contains("no copy"));
    }

    [Fact]
    public void An_unknown_slot_id_is_reported()
    {
        var copy = Copy(("headline", "Fill the slot"), ("body", "fine"), ("cta", "Try it"));

        CopyValidator.Validate(copy, Context()).ShouldContain(p => p.Contains("cta"));
    }

    [Fact]
    public void The_same_slot_written_twice_is_reported()
    {
        var copy = new CopySet
        {
            Slots =
            [
                new CopySlot { Id = "headline", Text = "First" },
                new CopySlot { Id = "headline", Text = "Second" },
                new CopySlot { Id = "body", Text = "fine" },
            ],
            FactCitations = [],
        };

        CopyValidator.Validate(copy, Context()).ShouldContain(p => p.Contains("twice"));
    }

    [Fact]
    public void Blank_text_in_a_present_slot_is_rejected()
    {
        var copy = Copy(("headline", "   "), ("body", "fine"));

        CopyValidator.Validate(copy, Context()).ShouldContain(p => p.Contains("headline") && p.Contains("no text"));
    }

    [Fact]
    public void A_citation_the_brief_did_not_allow_is_rejected()
    {
        var copy = new CopySet
        {
            Slots = [.. Slots.Select(s => new CopySlot { Id = s.Id, Text = "fine" })],
            FactCitations = ["unlisted-key"],
        };

        CopyValidator.Validate(copy, Context(allowedFactKeys: ["public-booking-page"]))
            .ShouldContain(p => p.Contains("unlisted-key"));
    }

    [Fact]
    public void An_allowed_citation_is_accepted()
    {
        var copy = new CopySet
        {
            Slots = [.. Slots.Select(s => new CopySlot { Id = s.Id, Text = "fine" })],
            FactCitations = ["public-booking-page"],
        };

        CopyValidator.Validate(copy, Context(allowedFactKeys: ["public-booking-page"])).ShouldBeEmpty();
    }

    [Fact]
    public void A_banned_word_is_caught_regardless_of_case()
    {
        var copy = Copy(("headline", "A REVOLUTIONARY idea"), ("body", "fine"));

        CopyValidator.Validate(copy, Context(bannedWords: ["revolutionary"]))
            .ShouldContain(p => p.Contains("revolutionary"));
    }

    [Fact]
    public void A_banned_word_only_matches_whole_words()
    {
        // "class" must not trip a ban on "ass" — literal words, not substrings.
        var copy = Copy(("headline", "First class service"), ("body", "fine"));

        CopyValidator.Validate(copy, Context(bannedWords: ["ass"])).ShouldBeEmpty();
    }

    [Fact]
    public void No_banned_words_configured_means_nothing_is_checked()
    {
        var copy = Copy(("headline", "Anything goes here"), ("body", "fine"));

        CopyValidator.Validate(copy, Context(bannedWords: [])).ShouldBeEmpty();
    }
}
