using ContentPilot.Application.Ai;
using ContentPilot.Infrastructure.Ai;
using Microsoft.Extensions.Options;
using Shouldly;

namespace ContentPilot.UnitTests.Ai;

/// <summary>
/// Cost arithmetic is enforcement, not reporting: the budget guard refuses calls based on
/// these numbers. Drift here spends real money.
/// </summary>
public sealed class ModelProfileTests
{
    private static readonly ModelProfile Opus = new()
    {
        Name = "strategist",
        Provider = ModelProvider.Anthropic,
        ModelId = "claude-opus-5",
        // $5.00 and $25.00 per million tokens, in micro-cents.
        InputPricePerMillion = 500_000_000,
        OutputPricePerMillion = 2_500_000_000,
        CacheReadPricePerMillion = 50_000_000,
        CacheWritePricePerMillion = 625_000_000,
    };

    [Fact]
    public void A_million_input_tokens_costs_five_dollars()
    {
        var cost = Opus.PriceOf(new TokenUsage(1_000_000, 0, 0, 0));

        (cost / 100_000_000m).ShouldBe(5.00m);
    }

    [Fact]
    public void A_million_output_tokens_costs_twenty_five_dollars() =>
        (Opus.PriceOf(new TokenUsage(0, 1_000_000, 0, 0)) / 100_000_000m).ShouldBe(25.00m);

    [Fact]
    public void Cached_reads_cost_a_tenth_of_fresh_input()
    {
        var fresh = Opus.PriceOf(new TokenUsage(1_000_000, 0, 0, 0));
        var cached = Opus.PriceOf(new TokenUsage(0, 0, 1_000_000, 0));

        // This ratio is the whole reason the brand block sits in the cacheable system half
        // of every prompt rather than in the user turn.
        cached.ShouldBe(fresh / 10);
    }

    [Fact]
    public void A_realistic_call_costs_a_fraction_of_a_cent()
    {
        // ~2k prompt, ~800 output: an ordinary copywriter call.
        var cost = Opus.PriceOf(new TokenUsage(2_000, 800, 0, 0));

        (cost / 100_000_000m).ShouldBe(0.0300m);
    }

    [Fact]
    public void Zero_usage_costs_nothing() =>
        Opus.PriceOf(TokenUsage.Empty).ShouldBe(0);

    [Fact]
    public void Costs_are_integers_so_thousands_of_calls_do_not_drift()
    {
        var single = Opus.PriceOf(new TokenUsage(333, 177, 0, 0));
        var thousand = Enumerable.Range(0, 1000).Sum(_ => single);

        // Summing floating-point fractions of a cent drifts, and a budget that drifts is a
        // budget that fails open.
        thousand.ShouldBe(single * 1000);
    }

    [Fact]
    public void An_unknown_profile_names_the_ones_that_exist()
    {
        var registry = Registry(("strategist", "claude-opus-5"), ("copywriter", "claude-opus-5"));

        var ex = Should.Throw<InvalidOperationException>(() => registry.Get("nonexistent"));

        ex.Message.ShouldContain("copywriter");
        ex.Message.ShouldContain("strategist");
    }

    [Fact]
    public void A_profile_defaults_to_anthropic_but_can_name_another_vendor()
    {
        var registry = new ModelProfileRegistry(Options.Create(new AiOptions
        {
            Profiles = new()
            {
                ["strategist"] = new ModelProfileOptions { ModelId = "claude-opus-5" },
                ["copywriter"] = new ModelProfileOptions
                {
                    Provider = ModelProvider.OpenAi,
                    ModelId = "gpt-5",
                },
            },
        }));

        // Per profile, not per deployment: comparing two vendors on the same brief is only
        // honest if everything except the model stays the same.
        registry.Get("strategist").Provider.ShouldBe(ModelProvider.Anthropic);
        registry.Get("copywriter").Provider.ShouldBe(ModelProvider.OpenAi);
    }

    [Fact]
    public void Profile_names_are_case_insensitive() =>
        Registry(("Strategist", "claude-opus-5")).Get("strategist").ModelId.ShouldBe("claude-opus-5");

    [Fact]
    public void An_empty_profile_set_fails_at_construction()
    {
        // Discovering this mid-campaign wastes whatever the run already spent.
        var options = Options.Create(new AiOptions { Profiles = [] });

        Should.Throw<InvalidOperationException>(() => new ModelProfileRegistry(options))
            .Message.ShouldContain("No model profiles configured");
    }

    [Fact]
    public void A_profile_without_a_model_id_fails_at_construction()
    {
        var options = Options.Create(new AiOptions
        {
            Profiles = new() { ["strategist"] = new ModelProfileOptions { ModelId = "" } },
        });

        Should.Throw<InvalidOperationException>(() => new ModelProfileRegistry(options))
            .Message.ShouldContain("strategist");
    }

    private static ModelProfileRegistry Registry(params (string Name, string ModelId)[] profiles) =>
        new(Options.Create(new AiOptions
        {
            Profiles = profiles.ToDictionary(
                p => p.Name,
                p => new ModelProfileOptions { ModelId = p.ModelId }),
        }));
}
