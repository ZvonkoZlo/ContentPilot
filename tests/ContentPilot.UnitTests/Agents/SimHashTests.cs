using ContentPilot.Application.ContentMemory;
using Shouldly;

namespace ContentPilot.UnitTests.Agents;

/// <summary>
/// The novelty check is a computation precisely because asking a model whether it is
/// repeating itself is asking the least reliable component to audit its own memory. That
/// only holds if the computation actually separates rephrasings from new ideas.
/// </summary>
public sealed class SimHashTests
{
    [Fact]
    public void The_same_text_hashes_identically() =>
        SimHash.Compute("Stop losing bookings to unanswered messages")
            .ShouldBe(SimHash.Compute("Stop losing bookings to unanswered messages"));

    [Fact]
    public void Word_order_barely_matters()
    {
        // Token-based by design: a reordered sentence is the same idea.
        var a = SimHash.Compute("clients book themselves at any hour");
        var b = SimHash.Compute("at any hour clients book themselves");

        SimHash.Distance(a, b).ShouldBe(0);
    }

    [Fact]
    public void A_rephrasing_is_recognised_as_a_near_duplicate()
    {
        var original = SimHash.Compute("Your chair sits empty when a client cancels late at night");
        var rephrased = SimHash.Compute("An empty chair when clients cancel late at night");

        SimHash.IsNearDuplicate(original, rephrased)
            .ShouldBeTrue($"distance was {SimHash.Distance(original, rephrased)}");
    }

    [Fact]
    public void A_genuinely_different_topic_is_not_a_duplicate()
    {
        var bookings = SimHash.Compute("Clients book themselves outside working hours");
        var staff = SimHash.Compute("Running one shared diary across four barbers");

        SimHash.IsNearDuplicate(bookings, staff)
            .ShouldBeFalse($"distance was {SimHash.Distance(bookings, staff)}");
    }

    [Fact]
    public void Croatian_diacritics_do_not_split_a_topic()
    {
        // "termin" and "termín" must not read as different subjects. Brands here write
        // Croatian, and diacritics are dropped in casual writing all the time.
        var withDiacritics = SimHash.Compute("Termini se popune i dok spavaš");
        var without = SimHash.Compute("Termini se popune i dok spavas");

        SimHash.Distance(withDiacritics, without).ShouldBe(0);
    }

    [Fact]
    public void Case_does_not_matter() =>
        SimHash.Distance(
            SimHash.Compute("NO-SHOWS COST A FULL CHAIR SLOT"),
            SimHash.Compute("no-shows cost a full chair slot")).ShouldBe(0);

    [Fact]
    public void Stop_words_do_not_make_unrelated_sentences_look_similar()
    {
        // Without stop-word removal two unrelated sentences share most of their tokens and
        // every comparison drifts toward "similar".
        var a = SimHash.Compute("The salon owner cannot answer the phone when she is cutting hair");
        var b = SimHash.Compute("The barber shop has a queue at five and nobody at ten");

        SimHash.IsNearDuplicate(a, b)
            .ShouldBeFalse($"distance was {SimHash.Distance(a, b)}");
    }

    [Fact]
    public void Empty_text_hashes_to_zero_rather_than_throwing() =>
        SimHash.Compute("   ").ShouldBe(0);

    [Fact]
    public void Distance_is_symmetric()
    {
        var a = SimHash.Compute("first topic about bookings");
        var b = SimHash.Compute("second topic about reminders");

        SimHash.Distance(a, b).ShouldBe(SimHash.Distance(b, a));
    }

    [Fact]
    public void The_threshold_sits_in_the_gap_between_rephrasings_and_new_ideas()
    {
        (string A, string B)[] rephrasings =
        [
            ("Your chair sits empty when a client cancels late at night", "An empty chair when clients cancel late at night"),
            ("Clients book outside working hours", "Clients can book after hours"),
            ("No-shows leave an empty chair", "An empty chair from a no-show"),
            ("Reminders delivered over WhatsApp", "WhatsApp reminders before an appointment"),
        ];

        (string A, string B)[] different =
        [
            ("Clients book outside working hours", "One shared diary for four staff"),
            ("No-shows leave an empty chair", "Reminders delivered over WhatsApp"),
            ("The salon owner cannot answer the phone while cutting hair", "A barber shop with a queue at five and nobody at ten"),
            ("Clients pick the service before choosing a time", "Every client has a record with their visit history"),
        ];

        // The corpus the threshold was derived from: rephrasings measured 0-18 apart,
        // different subjects 22-40. If a change to tokenisation or stemming moves these,
        // the threshold has to be re-derived from new measurements rather than nudged until
        // the suite goes green again.
        foreach (var (a, b) in rephrasings)
        {
            SimHash.Distance(SimHash.Compute(a), SimHash.Compute(b))
                .ShouldBeLessThanOrEqualTo(SimHash.NearDuplicateThreshold, $"'{a}' vs '{b}'");
        }

        foreach (var (a, b) in different)
        {
            SimHash.Distance(SimHash.Compute(a), SimHash.Compute(b))
                .ShouldBeGreaterThan(SimHash.NearDuplicateThreshold, $"'{a}' vs '{b}'");
        }
    }
}
