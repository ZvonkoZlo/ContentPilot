using ContentPilot.Application.Agents;
using ContentPilot.Application.Brand;
using ContentPilot.Application.ContentMemory;
using Shouldly;

namespace ContentPilot.UnitTests.Agents;

/// <summary>
/// These rules are all stated in the prompt too. That is not redundancy: the prompt is how
/// you get good output, and this is how you know you got it. A model that mostly follows
/// instructions still sometimes does not, and "mostly" is not a foundation for an
/// unattended weekly pipeline.
/// </summary>
public sealed class PlanValidatorTests
{
    private static PlanValidator.Context Context(
        int posts = 2, int carousels = 1, int reels = 1,
        IEnumerable<string>? excluded = null,
        IEnumerable<long>? recent = null,
        IEnumerable<DayOfWeek>? allowedDays = null) => new()
    {
        Quota = new QuotaView
        {
            Posts = posts,
            Carousels = carousels,
            Reels = reels,
            ExcludedTopics = (excluded ?? []).ToArray(),
        },
        KnownFactKeys = new HashSet<string> { "whatsapp-reminders", "public-booking-page", "multi-staff" },
        RecentTopicHashes = (recent ?? []).ToArray(),
        AllowedDays = (allowedDays ?? []).ToHashSet(),
    };

    private static PlannedItem Item(
        string type = "StaticPost",
        string topic = "Empty chairs when clients cancel late",
        string day = "Tuesday",
        params string[] factKeys) => new()
    {
        Type = type,
        Topic = topic,
        Pillar = "problem-solution",
        Objective = "Show the cost of an unanswered message",
        PublishDay = day,
        FactKeys = factKeys,
    };

    private static WeeklyPlan Plan(params PlannedItem[] items) => new()
    {
        Theme = "Bookings that happen without you",
        Items = items,
    };

    [Fact]
    public void A_correct_plan_raises_nothing()
    {
        var plan = Plan(
            Item(topic: "Empty chairs when clients cancel late"),
            Item(topic: "Booking requests lost in a full inbox"),
            Item("Carousel", "Setting up a shared diary for four barbers", factKeys: "multi-staff"),
            Item("Reel", "A client books at midnight while you sleep", factKeys: "public-booking-page"));

        PlanValidator.Validate(plan, Context()).ShouldBeEmpty();
    }

    [Fact]
    public void A_short_week_is_rejected_rather_than_quietly_accepted()
    {
        // Silently accepting three items would mean the operator gets a thin week and no
        // explanation for it.
        var problems = PlanValidator.Validate(Plan(Item(), Item(topic: "Another topic entirely")), Context());

        problems.ShouldContain(p => p.Contains("expected 1 Carousel item(s), got 0"));
        problems.ShouldContain(p => p.Contains("expected 1 Reel item(s), got 0"));
    }

    [Fact]
    public void An_extra_item_is_rejected_too() =>
        PlanValidator.Validate(
                Plan(Item(), Item(topic: "Second thing"), Item(topic: "Third thing"),
                     Item("Carousel", "Fourth thing"), Item("Reel", "Fifth thing")),
                Context())
            .ShouldContain(p => p.Contains("expected 2 StaticPost item(s), got 3"));

    [Fact]
    public void An_unknown_item_type_is_named() =>
        PlanValidator.Validate(Plan(Item("Story", "A topic")), Context(1, 0, 0))
            .ShouldContain(p => p.Contains("Unknown item type"));

    [Fact]
    public void An_excluded_topic_is_refused_because_it_is_the_operators_judgement()
    {
        var problems = PlanValidator.Validate(
            Plan(Item(topic: "Why our pricing beats the competition")),
            Context(1, 0, 0, excluded: ["competitor comparisons", "pricing"]));

        problems.ShouldContain(p => p.Contains("excluded topic"));
    }

    [Fact]
    public void An_exclusion_is_caught_in_the_objective_as_well_as_the_topic()
    {
        var item = Item(topic: "A perfectly innocent subject") with
        {
            Objective = "Explain why we are cheaper than the alternatives",
        };

        PlanValidator.Validate(Plan(item), Context(1, 0, 0, excluded: ["cheaper"]))
            .ShouldContain(p => p.Contains("excluded topic"));
    }

    [Fact]
    public void A_fabricated_fact_key_is_refused()
    {
        // The whole grounding chain rests on citations resolving. Catching it here is
        // cheaper than letting the copywriter build a claim on a key that does not exist.
        var problems = PlanValidator.Validate(
            Plan(Item(factKeys: "instant-payouts")),
            Context(1, 0, 0));

        problems.ShouldContain(p => p.Contains("which this brand does not have"));
    }

    [Fact]
    public void An_item_with_no_factual_claim_needs_no_citation() =>
        PlanValidator.Validate(Plan(Item()), Context(1, 0, 0)).ShouldBeEmpty();

    [Fact]
    public void A_publish_day_the_brand_does_not_use_is_refused() =>
        PlanValidator.Validate(
                Plan(Item(day: "Sunday")),
                Context(1, 0, 0, allowedDays: [DayOfWeek.Tuesday, DayOfWeek.Thursday]))
            .ShouldContain(p => p.Contains("publishes on Sunday"));

    [Fact]
    public void An_unrecognised_day_is_refused() =>
        PlanValidator.Validate(Plan(Item(day: "Someday")), Context(1, 0, 0))
            .ShouldContain(p => p.Contains("unrecognised publish day"));

    [Fact]
    public void A_topic_that_repeats_recent_content_is_refused()
    {
        var lastMonth = SimHash.Compute("An empty chair when clients cancel late at night");

        var problems = PlanValidator.Validate(
            Plan(Item(topic: "Your chair sits empty when a client cancels late at night")),
            Context(1, 0, 0, recent: [lastMonth]));

        problems.ShouldContain(p => p.Contains("repeats recent content"));
    }

    [Fact]
    public void A_genuinely_new_topic_passes_the_novelty_check() =>
        PlanValidator.Validate(
                Plan(Item(topic: "One shared diary across four barbers")),
                Context(1, 0, 0, recent: [SimHash.Compute("Reminders delivered over WhatsApp")]))
            .ShouldBeEmpty();

    [Fact]
    public void Two_items_that_are_the_same_idea_twice_are_caught()
    {
        // A model asked for several items will sometimes produce one idea wearing two
        // coats. Nothing outside the plan would notice.
        var problems = PlanValidator.Validate(
            Plan(Item(topic: "Clients book outside working hours"),
                 Item(topic: "Clients can book after hours")),
            Context(2, 0, 0));

        problems.ShouldContain(p => p.Contains("same idea in different words"));
    }

    [Fact]
    public void A_plan_with_no_theme_is_refused() =>
        PlanValidator.Validate(Plan(Item()) with { Theme = "  " }, Context(1, 0, 0))
            .ShouldContain(p => p.Contains("no theme"));

    [Fact]
    public void An_item_with_no_objective_is_refused() =>
        PlanValidator.Validate(Plan(Item() with { Objective = "" }), Context(1, 0, 0))
            .ShouldContain(p => p.Contains("no objective"));

    [Fact]
    public void Every_problem_is_reported_at_once_rather_than_one_per_round_trip()
    {
        // Each round trip costs a model call. Reporting one failure at a time would turn a
        // single bad plan into several billable retries.
        var problems = PlanValidator.Validate(
            Plan(Item(topic: "Why our pricing wins", day: "Someday", factKeys: "made-up-key")),
            Context(2, 1, 1, excluded: ["pricing"]));

        problems.Count.ShouldBeGreaterThanOrEqualTo(5);
    }
}
