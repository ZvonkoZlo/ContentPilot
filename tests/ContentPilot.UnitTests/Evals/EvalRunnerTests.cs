using ContentPilot.Application.Evals;
using ContentPilot.Domain.Evals;
using Shouldly;

namespace ContentPilot.UnitTests.Evals;

/// <summary>
/// The runner's own shape, exercised with fake scenarios rather than the real catalogue —
/// what matters here is that pass/fail counts and per-scenario rows come out right, not
/// what any particular scenario asserts.
/// </summary>
public sealed class EvalRunnerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 6, 0, 0, TimeSpan.Zero);

    private sealed class FakeScenario(string name, bool passed, string detail = "") : IEvalScenario
    {
        public string Name => name;
        public EvalScenarioKind Kind => EvalScenarioKind.Deterministic;
        public Task<EvalScenarioOutcome> RunAsync(CancellationToken ct) =>
            Task.FromResult(new EvalScenarioOutcome(passed, detail));
    }

    [Fact]
    public async Task A_run_of_all_passing_scenarios_is_recorded_as_all_passed()
    {
        var (run, results) = await EvalRunner.RunAsync(
            [new FakeScenario("a", true), new FakeScenario("b", true)], EvalMode.Cassette, Now);

        run.TotalScenarios.ShouldBe(2);
        run.PassedScenarios.ShouldBe(2);
        run.AllPassed.ShouldBeTrue();
        results.Count.ShouldBe(2);
        results.ShouldAllBe(r => r.Passed);
        results.ShouldAllBe(r => r.EvalRunId == run.Id);
    }

    [Fact]
    public async Task A_single_failing_scenario_is_reflected_in_the_runs_own_count()
    {
        var (run, results) = await EvalRunner.RunAsync(
            [new FakeScenario("a", true), new FakeScenario("b", false, "expected X, got Y")], EvalMode.Cassette, Now);

        run.TotalScenarios.ShouldBe(2);
        run.PassedScenarios.ShouldBe(1);
        run.AllPassed.ShouldBeFalse();
        results.Single(r => r.ScenarioName == "b").Passed.ShouldBeFalse();
        results.Single(r => r.ScenarioName == "b").Detail.ShouldBe("expected X, got Y");
    }

    [Fact]
    public async Task An_empty_catalogue_is_not_reported_as_all_passed()
    {
        // Zero scenarios trivially satisfies "every scenario passed" by vacuous truth, which
        // would make a broken discovery step (nothing ran) look identical to a clean run.
        var (run, _) = await EvalRunner.RunAsync([], EvalMode.Cassette, Now);

        run.TotalScenarios.ShouldBe(0);
        run.AllPassed.ShouldBeFalse();
    }

    [Fact]
    public async Task The_run_carries_the_mode_it_was_asked_to_run_in()
    {
        var (run, _) = await EvalRunner.RunAsync([new FakeScenario("a", true)], EvalMode.Live, Now);

        run.Mode.ShouldBe(EvalMode.Live);
        run.RunAt.ShouldBe(Now);
    }
}
