using ContentPilot.Application.Abstractions;
using ContentPilot.Domain.Evals;
using ContentPilot.Domain.Workflow;
using ContentPilot.Infrastructure.Jobs;
using ContentPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ContentPilot.Api.Endpoints;

/// <summary>
/// Phase 9's "any campaign can be explained end to end" starts with an operator being able
/// to see what is stuck, before any dashboard exists to make it pretty. Read-only,
/// deliberately: a "resume" or "re-run" action is a real decision (does the stuck run's
/// lease owner still exist? was the failure transient or a bug that will just fail again?)
/// that belongs behind its own considered endpoint, not bundled into a visibility sweep.
/// Cross-tenant by design — an operator's job is to see across every tenant at once.
/// </summary>
public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin").WithTags("Admin");

        group.MapGet("/dead-jobs", async (AppDbContext db, IMutableTenantContext tenantContext, int limit, CancellationToken ct) =>
        {
            using var _ = tenantContext.BeginCrossTenantScope();

            var jobs = await db.Jobs
                .AsNoTracking()
                .Where(j => j.State == JobState.Dead)
                .OrderByDescending(j => j.CreatedAt)
                .Take(limit is > 0 and <= 500 ? limit : 100)
                .Select(j => new DeadJobResponse(j.Id, j.Type, j.TenantId, j.Attempts, j.MaxAttempts, j.LastError, j.CreatedAt, j.StartedAt))
                .ToListAsync(ct);

            return Results.Ok(jobs);
        })
        .WithSummary(
            "Jobs that exhausted every retry and are permanently Dead — attempts, the last " +
            "error, and which tenant, if any. §9's job-queue half of \"admin views: dead jobs\".");

        group.MapGet("/stuck-runs", async (AppDbContext db, IMutableTenantContext tenantContext, IClock clock, CancellationToken ct) =>
        {
            using var _ = tenantContext.BeginCrossTenantScope();

            var now = clock.UtcNow;

            var runs = await db.WorkflowRuns
                .AsNoTracking()
                .Where(r => r.State == WorkflowRunState.Active && r.Deadline < now)
                .OrderBy(r => r.Deadline)
                .Select(r => new StuckRunResponse(
                    r.Id, r.TenantId, r.Scope.ToString(), r.CampaignId, r.EntityId,
                    r.Deadline, r.LeaseUntil, r.LeaseOwner, r.StepsExecuted))
                .ToListAsync(ct);

            return Results.Ok(runs);
        })
        .WithSummary(
            "Workflow runs still Active past their own wall-clock deadline — the reaper " +
            "should be resolving these on its own schedule; a run appearing here for more " +
            "than one reaper interval means the reaper itself needs attention, not the run.");

        group.MapGet("/eval-runs", async (AppDbContext db, int limit, CancellationToken ct) =>
        {
            var runs = await db.EvalRuns
                .AsNoTracking()
                .OrderByDescending(r => r.RunAt)
                .Take(limit is > 0 and <= 200 ? limit : 50)
                .ToListAsync(ct);

            var runIds = runs.Select(r => r.Id).ToArray();
            var results = await db.EvalResults
                .AsNoTracking()
                .Where(r => runIds.Contains(r.EvalRunId))
                .OrderBy(r => r.ScenarioName)
                .ToListAsync(ct);
            var byRun = results.ToLookup(r => r.EvalRunId);

            return Results.Ok(runs.Select(run => new EvalRunResponse(
                run.Id,
                run.Mode.ToString(),
                run.RunAt,
                run.TotalScenarios,
                run.PassedScenarios,
                run.AllPassed,
                byRun[run.Id].Select(result => new EvalResultResponse(
                    result.ScenarioName,
                    result.Kind.ToString(),
                    result.Passed,
                    result.Detail,
                    result.DurationMs)).ToList())));
        })
        .WithSummary("Lists recent eval runs with their scenario results for Phase 9's regression trend view.");

        return app;
    }
}

public sealed record DeadJobResponse(
    Guid Id, string Type, Guid? TenantId, int Attempts, int MaxAttempts, string? LastError, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt);

public sealed record StuckRunResponse(
    Guid Id, Guid TenantId, string Scope, Guid CampaignId, Guid EntityId,
    DateTimeOffset Deadline, DateTimeOffset? LeaseUntil, string? LeaseOwner, int StepsExecuted);

public sealed record EvalRunResponse(
    Guid Id, string Mode, DateTimeOffset RunAt, int TotalScenarios, int PassedScenarios,
    bool AllPassed, IReadOnlyList<EvalResultResponse> Results);

public sealed record EvalResultResponse(
    string ScenarioName, string Kind, bool Passed, string Detail, int DurationMs);
