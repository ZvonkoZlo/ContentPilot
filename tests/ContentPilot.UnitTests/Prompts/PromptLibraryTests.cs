using ContentPilot.Application.Prompts;
using Shouldly;

namespace ContentPilot.UnitTests.Prompts;

/// <summary>
/// Prompts are the least testable part of an LLM system, so the parts that <em>are</em>
/// mechanical get tested hard: that they load, that their variables line up, and that a
/// mismatch fails loudly instead of sending the model a literal <c>{{brand_block}}</c>.
/// </summary>
public sealed class PromptLibraryTests
{
    private static readonly PromptLibrary Library = PromptLibrary.LoadEmbedded();

    [Fact]
    public void Every_embedded_prompt_loads()
    {
        Library.All.ShouldNotBeEmpty();
        Library.All.ShouldAllBe(p => p.System.Length > 0 && p.User.Length > 0);
    }

    [Fact]
    public void The_strategist_prompt_declares_its_profile_and_schema()
    {
        var prompt = Library.Get("content-strategist");

        prompt.ModelProfile.ShouldBe("strategist");
        prompt.SchemaName.ShouldBe("WeeklyPlan");
        prompt.Version.ShouldBe(1);
        prompt.Reference.ShouldBe("content-strategist@v1");
    }

    [Fact]
    public void A_prompt_hashes_stably()
    {
        var first = Library.Get("content-strategist").ContentHash;
        var second = PromptLibrary.LoadEmbedded().Get("content-strategist").ContentHash;

        // Every AgentRun points at a version by hash; a hash that moved on its own would
        // make the audit trail meaningless.
        second.ShouldBe(first);
        first.Length.ShouldBe(64);
    }

    [Fact]
    public void An_unknown_prompt_names_the_ones_that_exist() =>
        Should.Throw<InvalidOperationException>(() => Library.Get("no-such-prompt"))
            .Message.ShouldContain("content-strategist");

    [Fact]
    public void Rendering_substitutes_every_placeholder()
    {
        var prompt = Library.Get("content-strategist");
        var variables = prompt.RequiredVariables().ToDictionary(name => name, name => $"<{name}>");

        var (system, user) = prompt.Render(variables);

        (system + user).ShouldNotContain("{{");
        user.ShouldContain("<brand_block>");
    }

    [Fact]
    public void A_missing_variable_is_refused_rather_than_sent_to_the_model()
    {
        var prompt = Library.Get("content-strategist");

        // Otherwise the literal text "{{brand_block}}" reaches the model, which will not
        // complain, and every reader downstream misreads the result as working.
        var ex = Should.Throw<PromptRenderException>(
            () => prompt.Render(new Dictionary<string, string> { ["week_start"] = "2026-09-14" }));

        ex.Message.ShouldContain("brand_block");
    }

    [Fact]
    public void An_unused_variable_is_refused_because_it_is_usually_a_half_applied_rename()
    {
        var prompt = Library.Get("content-strategist");
        var variables = prompt.RequiredVariables().ToDictionary(name => name, name => "x");
        variables["a_variable_nobody_uses"] = "x";

        Should.Throw<PromptRenderException>(() => prompt.Render(variables))
            .Message.ShouldContain("a_variable_nobody_uses");
    }

    [Fact]
    public void Every_prompt_names_a_profile_that_exists_in_configuration()
    {
        // The profiles configured in appsettings.json. A prompt naming a profile nobody
        // configured fails at the first call rather than at startup, which is too late.
        string[] configured = ["strategist", "creative-director", "copywriter", "marketing-qa"];

        foreach (var prompt in Library.All)
        {
            configured.ShouldContain(prompt.ModelProfile,
                $"{prompt.Reference} names profile '{prompt.ModelProfile}'.");
        }
    }
}
