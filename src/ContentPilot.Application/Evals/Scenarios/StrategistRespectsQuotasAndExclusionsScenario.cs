using ContentPilot.Application.Agents;
using ContentPilot.Application.Brand;
using ContentPilot.Domain.Evals;

namespace ContentPilot.Application.Evals.Scenarios;

/// <summary>
/// §27's catalogue: "Strategist respects quotas and exclusions — exact type counts; zero
/// excluded topics." Two independent checks in one scenario: a plan short one StaticPost is
/// rejected on count, and a plan that proposes an excluded topic is rejected on content —
/// both through <see cref="PlanValidator"/> directly, since both are pure computation.
/// </summary>
public sealed class StrategistRespectsQuotasAndExclusionsScenario : IEvalScenario
{
    public string Name => "Strategist respects quotas and exclusions";

    public EvalScenarioKind Kind => EvalScenarioKind.Deterministic;

    public Task<EvalScenarioOutcome> RunAsync(CancellationToken ct)
    {
        var context = new PlanValidator.Context
        {
            Quota = new QuotaView { Posts = 2, Carousels = 0, Reels = 0, ExcludedTopics = ["free trials"] },
            KnownFactKeys = new HashSet<string>(),
            RecentTopicHashes = [],
        };

        var shortPlan = new WeeklyPlan
        {
            Theme = "Only one post, quota wants two",
            Items = [Item("A lone StaticPost")],
        };

        var shortPlanProblems = PlanValidator.Validate(shortPlan, context);

        if (shortPlanProblems.Count == 0)
        {
            return Task.FromResult(EvalScenarioOutcome.Fail(
                "A plan short one StaticPost against a quota of two was accepted — the quota check may have regressed."));
        }

        var excludedPlan = new WeeklyPlan
        {
            Theme = "Mentions a topic the brand excluded",
            Items = [Item("A lone StaticPost"), Item("Ask us about our free trials this month")],
        };

        var excludedPlanProblems = PlanValidator.Validate(excludedPlan, context);
        var caughtExclusion = excludedPlanProblems.Any(p => p.Contains("exclud", StringComparison.OrdinalIgnoreCase));

        return Task.FromResult(caughtExclusion
            ? EvalScenarioOutcome.Pass($"Both violations caught: {string.Join("; ", shortPlanProblems.Concat(excludedPlanProblems))}")
            : EvalScenarioOutcome.Fail(
                "A plan mentioning an excluded topic was not rejected — the exclusion check may have regressed."));
    }

    private static PlannedItem Item(string topic) => new()
    {
        Type = "StaticPost",
        Topic = topic,
        Pillar = "problem-solution",
        Objective = "n/a",
        PublishDay = "Tuesday",
        FactKeys = [],
    };
}
