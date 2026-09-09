using ContentPilot.Application.Agents;
using ContentPilot.Application.ContentMemory;
using ContentPilot.Application.Prompts;
using ContentPilot.UnitTests.BrandBrain;
using Shouldly;

namespace ContentPilot.UnitTests.Agents;

/// <summary>
/// The agent is a pure function of its input, which is what makes it testable at all:
/// no key, no network, no database. What it puts in front of the model, and what it accepts
/// back, are both assertable here.
/// </summary>
public sealed class ContentStrategistAgentTests
{
    private static readonly ContentStrategistAgent Agent = new();
    private static readonly PromptLibrary Prompts = PromptLibrary.LoadEmbedded();
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 6, 0, 0, TimeSpan.Zero);

    private static StrategistInput Input(
        IEnumerable<RecentContent>? recent = null,
        string? extraDirection = null) => new()
    {
        Brand = Application.Brand.BrandBrainAssembler.Assemble(Fixture.Input(), Now),
        RecentContent = (recent ?? []).ToArray(),
        WeekStart = new DateOnly(2026, 9, 14),
        ExtraDirection = extraDirection,
    };

    private static RecentContent Recent(string topic, int? rating = null) => new()
    {
        Topic = topic,
        Pillar = "problem-solution",
        TopicHash = SimHash.Compute(topic),
        ApprovedAt = Now.AddDays(-14),
        HumanRating = rating,
    };

    [Fact]
    public void Its_variables_satisfy_its_prompt_exactly()
    {
        var variables = Agent.BuildVariables(Input());

        // Strict in both directions, so a rename applied on one side only fails here rather
        // than sending the model a literal placeholder.
        Should.NotThrow(() => Prompts.Get(Agent.PromptId).Render(variables));
    }

    [Fact]
    public void The_brief_carries_the_quota_the_validator_will_enforce()
    {
        var variables = Agent.BuildVariables(Input());

        // The prompt asks for exact counts and the validator checks them; if these two
        // disagreed, every plan would fail validation for a reason nobody could see.
        variables["posts"].ShouldBe("2");
        variables["carousels"].ShouldBe("1");
        variables["reels"].ShouldBe("1");
    }

    [Fact]
    public void Asset_identifiers_are_left_out_of_the_strategists_brief()
    {
        var block = Agent.BuildVariables(Input())["brand_block"];

        // It chooses subjects, not pictures. Those lines would be tokens spent on nothing.
        block.ShouldNotContain("Available assets");
        block.ShouldContain("Product facts");
    }

    [Fact]
    public void A_first_week_says_so_rather_than_showing_an_empty_list()
    {
        Agent.BuildVariables(Input())["recent_content"]
            .ShouldContain("first week");
    }

    [Fact]
    public void Recent_content_is_listed_compactly_with_its_rating()
    {
        var variables = Agent.BuildVariables(Input(
        [
            Recent("Empty chairs when clients cancel late", rating: 4),
            Recent("One shared diary across four barbers"),
        ]));

        var recent = variables["recent_content"];

        var lines = recent.Split('\n');

        // The operator's rating is the only signal that says whether any of this works, so
        // it goes in front of the model where it exists - and nothing is invented where it
        // does not.
        lines[0].ShouldContain("Empty chairs when clients cancel late");
        lines[0].ShouldContain("rated 4/5");
        lines[1].ShouldContain("One shared diary");
        lines[1].ShouldNotContain("rated");
    }

    [Fact]
    public void An_operator_steer_reaches_the_brief()
    {
        Agent.BuildVariables(Input(extraDirection: "We launch WhatsApp reminders on Thursday."))
            ["extra_direction"].ShouldContain("WhatsApp reminders on Thursday");
    }

    [Fact]
    public void No_steer_leaves_the_slot_empty_rather_than_saying_none()
    {
        // "No extra direction" is an instruction the model would try to act on.
        Agent.BuildVariables(Input())["extra_direction"].ShouldBeEmpty();
    }

    [Fact]
    public void A_plan_that_matches_the_brief_is_accepted()
    {
        var plan = new WeeklyPlan
        {
            Theme = "Bookings that happen without you",
            Items =
            [
                Item("StaticPost", "Empty chairs when clients cancel late"),
                Item("StaticPost", "Booking requests lost in a full inbox"),
                Item("Carousel", "One shared diary across four barbers", "no-account-needed"),
                Item("Reel", "A client books at midnight while you sleep", "public-booking-page"),
            ],
        };

        Agent.Validate(plan, Input()).ShouldBeEmpty();
    }

    [Fact]
    public void A_plan_repeating_recent_content_is_rejected_by_the_agent_itself()
    {
        var input = Input([Recent("An empty chair when clients cancel late at night")]);

        var plan = new WeeklyPlan
        {
            Theme = "Bookings that happen without you",
            Items =
            [
                Item("StaticPost", "Your chair sits empty when a client cancels late at night"),
                Item("StaticPost", "Booking requests lost in a full inbox"),
                Item("Carousel", "One shared diary across four barbers"),
                Item("Reel", "A client books at midnight while you sleep"),
            ],
        };

        Agent.Validate(plan, input).ShouldContain(p => p.Contains("repeats recent content"));
    }

    [Fact]
    public void A_fabricated_fact_key_is_rejected_by_the_agent_itself() =>
        Agent.Validate(
                new WeeklyPlan
                {
                    Theme = "x",
                    Items =
                    [
                        Item("StaticPost", "First topic", "instant-payouts"),
                        Item("StaticPost", "Second topic"),
                        Item("Carousel", "Third topic"),
                        Item("Reel", "Fourth topic"),
                    ],
                },
                Input())
            .ShouldContain(p => p.Contains("which this brand does not have"));

    [Fact]
    public void The_agent_declares_a_prompt_and_schema_that_exist()
    {
        Should.NotThrow(() => Prompts.Get(Agent.PromptId));
        Agent.ResponseSchema.ShouldContain("\"theme\"");
        Agent.ResponseSchema.ShouldContain("additionalProperties");
    }

    private static PlannedItem Item(string type, string topic, params string[] factKeys) => new()
    {
        Type = type,
        Topic = topic,
        Pillar = "problem-solution",
        Objective = "Show the cost of an unanswered message",
        PublishDay = "Tuesday",
        FactKeys = factKeys,
    };
}
