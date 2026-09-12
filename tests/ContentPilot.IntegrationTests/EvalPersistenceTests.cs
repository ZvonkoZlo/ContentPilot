using ContentPilot.Application.Evals;
using ContentPilot.Application.Evals.Scenarios;
using ContentPilot.Domain.Evals;
using ContentPilot.Infrastructure.Persistence;
using ContentPilot.IntegrationTests.Infrastructure;
using ContentPilot.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace ContentPilot.IntegrationTests;

/// <summary>
/// §27's real scenario catalogue, run in cassette mode and persisted to real Postgres — not
/// tenant-scoped, since an eval run is a statement about the pipeline's own behaviour, the
/// same reason <see cref="EvalRun"/> isn't <c>ITenantOwned</c>.
/// </summary>
[Collection(ContentPilotCollection.Name)]
public sealed class EvalPersistenceTests(ContentPilotFixture fixture)
{
    private static readonly IReadOnlyList<IEvalScenario> Catalogue =
    [
        new StrategistAvoidsRecentTopicsScenario(),
        new StrategistRespectsQuotasAndExclusionsScenario(),
        new CopywriterRespectsSlotBudgetsScenario(),
    ];

    [DockerFact]
    public async Task The_full_catalogue_passes_and_persists_one_row_per_scenario()
    {
        await using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var (run, results) = await EvalRunner.RunAsync(Catalogue, EvalMode.Cassette, DateTimeOffset.UtcNow);

        db.EvalRuns.Add(run);
        db.EvalResults.AddRange(results);
        await db.SaveChangesAsync();

        var reloadedRun = await db.EvalRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id);
        reloadedRun.AllPassed.ShouldBeTrue();
        reloadedRun.TotalScenarios.ShouldBe(Catalogue.Count);
        reloadedRun.PassedScenarios.ShouldBe(Catalogue.Count);

        var reloadedResults = await db.EvalResults.AsNoTracking().Where(r => r.EvalRunId == run.Id).ToListAsync();
        reloadedResults.Count.ShouldBe(Catalogue.Count);
        reloadedResults.ShouldAllBe(r => r.Passed);
        reloadedResults.Select(r => r.ScenarioName).ShouldBe(Catalogue.Select(s => s.Name), ignoreOrder: true);
    }
}
