using System.Diagnostics;
using ContentPilot.Domain.Evals;

namespace ContentPilot.Application.Evals;

/// <summary>
/// Runs a scenario catalogue and shapes the result into the two rows §27 asks for — one
/// <see cref="EvalRun"/>, one <see cref="EvalResult"/> per scenario. Pure: it has no opinion
/// on where those rows end up, so the same runner serves a unit test asserting the shape and
/// a job or CLI command that actually persists them.
/// </summary>
public static class EvalRunner
{
    public static async Task<(EvalRun Run, IReadOnlyList<EvalResult> Results)> RunAsync(
        IReadOnlyList<IEvalScenario> scenarios, EvalMode mode, DateTimeOffset now, CancellationToken ct = default)
    {
        var outcomes = new List<(IEvalScenario Scenario, EvalScenarioOutcome Outcome, int DurationMs)>();

        foreach (var scenario in scenarios)
        {
            var stopwatch = Stopwatch.StartNew();
            var outcome = await scenario.RunAsync(ct);
            stopwatch.Stop();

            outcomes.Add((scenario, outcome, (int)stopwatch.ElapsedMilliseconds));
        }

        var run = new EvalRun(mode, now, outcomes.Count, outcomes.Count(o => o.Outcome.Passed));

        var results = outcomes
            .Select(o => new EvalResult(run.Id, o.Scenario.Name, o.Scenario.Kind, o.Outcome.Passed, o.Outcome.Detail, o.DurationMs))
            .ToList();

        return (run, results);
    }
}
