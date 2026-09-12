using ContentPilot.Application.Agents;
using ContentPilot.Application.Brand;
using ContentPilot.Application.ContentMemory;
using ContentPilot.Domain.Evals;

namespace ContentPilot.Application.Evals.Scenarios;

/// <summary>
/// §27's catalogue: "Strategist avoids recent topics — no proposed topic within Hamming 6
/// of the 12-week seeded history." Deterministic, so it runs against <see cref="PlanValidator"/>
/// directly rather than a live model — the assertion is about the validator's own novelty
/// check firing correctly, which is exactly what a cassette-mode eval is for: catching a
/// regression in the check itself, not judging any particular model's output.
/// </summary>
public sealed class StrategistAvoidsRecentTopicsScenario : IEvalScenario
{
    public string Name => "Strategist avoids recent topics";

    public EvalScenarioKind Kind => EvalScenarioKind.Deterministic;

    public Task<EvalScenarioOutcome> RunAsync(CancellationToken ct)
    {
        const string repeatedTopic = "Your chair sits empty when someone cancels at 9pm";

        var context = new PlanValidator.Context
        {
            Quota = new QuotaView { Posts = 1, Carousels = 0, Reels = 0 },
            KnownFactKeys = new HashSet<string>(),
            RecentTopicHashes = [SimHash.Compute(repeatedTopic)],
        };

        var plan = new WeeklyPlan
        {
            Theme = "A week that repeats itself",
            Items = [new PlannedItem
            {
                Type = "StaticPost",
                Topic = repeatedTopic,
                Pillar = "problem-solution",
                Objective = "n/a",
                PublishDay = "Tuesday",
                FactKeys = [],
            }],
        };

        var problems = PlanValidator.Validate(plan, context);
        var caught = problems.Any(p => p.Contains("recent", StringComparison.OrdinalIgnoreCase)
            || p.Contains("novel", StringComparison.OrdinalIgnoreCase)
            || p.Contains("repeat", StringComparison.OrdinalIgnoreCase));

        return Task.FromResult(caught
            ? EvalScenarioOutcome.Pass($"Rejected as expected: {string.Join("; ", problems)}")
            : EvalScenarioOutcome.Fail(
                "An exact repeat of a recently-seeded topic was not rejected — the novelty " +
                "check may have regressed."));
    }
}
