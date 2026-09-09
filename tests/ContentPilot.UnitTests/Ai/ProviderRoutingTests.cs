using ContentPilot.Application.Ai;
using ContentPilot.Infrastructure.Ai;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace ContentPilot.UnitTests.Ai;

/// <summary>
/// Each vendor gets its own adapter on its own official SDK. Nothing is routed through
/// another vendor's compatibility shim, which would quietly forfeit schema enforcement and
/// misreport which model actually ran.
/// </summary>
public sealed class ProviderRoutingTests
{
    private static AiOptions Options(bool enabled = true) => new()
    {
        Enabled = enabled,
        Profiles = new()
        {
            ["strategist"] = new ModelProfileOptions
            {
                Provider = ModelProvider.Anthropic,
                ModelId = "claude-opus-5",
            },
            ["copywriter"] = new ModelProfileOptions
            {
                Provider = ModelProvider.OpenAi,
                ModelId = "gpt-5",
            },
        },
    };

    private static (ProviderRoutingLanguageModelClient Router, ModelProfileRegistry Registry) Build(AiOptions options)
    {
        var wrapped = Microsoft.Extensions.Options.Options.Create(options);
        var registry = new ModelProfileRegistry(wrapped);

        var router = new ProviderRoutingLanguageModelClient(
            registry,
            new AnthropicLanguageModelClient(registry, wrapped, NullLogger<AnthropicLanguageModelClient>.Instance),
            new OpenAiLanguageModelClient(registry, wrapped, NullLogger<OpenAiLanguageModelClient>.Instance));

        return (router, registry);
    }

    private static LlmRequest Request(string profile) => new()
    {
        Profile = profile,
        System = "s",
        User = "u",
        ResponseSchema = """{"type":"object"}""",
        Operation = "probe",
    };

    [Fact]
    public async Task An_openai_profile_reaches_the_openai_adapter()
    {
        var (router, _) = Build(Options());

        // No key is configured, so the OpenAI adapter is the one that says so. Reaching
        // that message proves the request was routed there rather than to Anthropic.
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => router.CompleteAsync(Request("copywriter")));

        ex.Message.ShouldContain("OpenAI key");
        ex.Message.ShouldContain("Ai__Providers__OpenAi__ApiKey");
    }

    [Fact]
    public async Task An_unknown_profile_fails_before_any_adapter_is_chosen()
    {
        var (router, _) = Build(Options());

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => router.CompleteAsync(Request("nonexistent")));

        ex.Message.ShouldContain("No model profile");
    }

    [Fact]
    public async Task A_disabled_layer_cannot_spend_whatever_keys_are_set()
    {
        var (router, _) = Build(Options(enabled: false));

        // Ai:Enabled is the master switch, and it is checked inside each adapter rather
        // than only at the edge, so no path around it can quietly reach a vendor.
        foreach (var profile in new[] { "strategist", "copywriter" })
        {
            var ex = await Should.ThrowAsync<InvalidOperationException>(
                () => router.CompleteAsync(Request(profile)));

            ex.Message.ShouldContain("disabled");
        }
    }

    [Fact]
    public void An_openai_profile_is_priced_by_its_own_rates()
    {
        var (_, registry) = Build(new AiOptions
        {
            Enabled = true,
            Profiles = new()
            {
                ["copywriter"] = new ModelProfileOptions
                {
                    Provider = ModelProvider.OpenAi,
                    ModelId = "gpt-5",
                    InputPricePerMillion = 125_000_000,
                    OutputPricePerMillion = 1_000_000_000,
                },
            },
        });

        // Prices belong to the profile, so the ledger stays correct across vendors with
        // completely different rate cards.
        var cost = registry.Get("copywriter").PriceOf(new TokenUsage(1_000_000, 0, 0, 0));

        (cost / 100_000_000m).ShouldBe(1.25m);
    }
}
