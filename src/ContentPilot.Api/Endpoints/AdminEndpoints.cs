using ContentPilot.Application.Abstractions;
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

        return app;
    }
}

public sealed record DeadJobResponse(
    Guid Id, string Type, Guid? TenantId, int Attempts, int MaxAttempts, string? LastError, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt);

public sealed record StuckRunResponse(
    Guid Id, Guid TenantId, string Scope, Guid CampaignId, Guid EntityId,
    DateTimeOffset Deadline, DateTimeOffset? LeaseUntil, string? LeaseOwner, int StepsExecuted);
