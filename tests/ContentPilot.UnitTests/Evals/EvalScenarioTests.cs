using ContentPilot.Application.Evals.Scenarios;
using Shouldly;

namespace ContentPilot.UnitTests.Evals;

/// <summary>
/// §27's deterministic scenarios, run directly. Each one is a pure regression guard on an
/// existing validator — the assertion is "the check still fires", not "the model is good".
/// </summary>
public sealed class EvalScenarioTests
{
    [Fact]
    public async Task Strategist_avoids_recent_topics_scenario_passes_against_a_working_novelty_check()
    {
        var outcome = await new StrategistAvoidsRecentTopicsScenario().RunAsync(default);

        outcome.Passed.ShouldBeTrue(outcome.Detail);
    }

    [Fact]
    public async Task Strategist_respects_quotas_and_exclusions_scenario_passes_against_working_validators()
    {
        var outcome = await new StrategistRespectsQuotasAndExclusionsScenario().RunAsync(default);

        outcome.Passed.ShouldBeTrue(outcome.Detail);
    }

    [Fact]
    public async Task Copywriter_respects_slot_budgets_scenario_passes_against_a_working_budget_check()
    {
        var outcome = await new CopywriterRespectsSlotBudgetsScenario().RunAsync(default);

        outcome.Passed.ShouldBeTrue(outcome.Detail);
    }

    [Fact]
    public void Every_scenario_names_the_kind_the_plans_own_catalogue_gives_it()
    {
        IEnumerable<(string Name, string Kind)> scenarios =
        [
            (new StrategistAvoidsRecentTopicsScenario().Name, new StrategistAvoidsRecentTopicsScenario().Kind.ToString()),
            (new StrategistRespectsQuotasAndExclusionsScenario().Name, new StrategistRespectsQuotasAndExclusionsScenario().Kind.ToString()),
            (new CopywriterRespectsSlotBudgetsScenario().Name, new CopywriterRespectsSlotBudgetsScenario().Kind.ToString()),
        ];

        scenarios.ShouldAllBe(s => s.Kind == "Deterministic");
    }
}
