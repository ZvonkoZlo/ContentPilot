using System.Text;
using ContentPilot.Application.Abstractions;
using ContentPilot.Application.Jobs;
using ContentPilot.Infrastructure.Jobs;
using ContentPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ContentPilot.Api.Endpoints;

/// <summary>
/// The walking skeleton's proof endpoints: enqueue a job, round-trip an object, inspect
/// the queue. They stay in the codebase as the fastest way to tell whether an
/// environment is actually wired up.
/// </summary>
public static class DiagnosticsEndpoints
{
    public static IEndpointRouteBuilder MapDiagnosticsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/diagnostics").WithTags("Diagnostics");

        group.MapPost("/ping", async (
            PingRequest? request,
            IJobQueue queue,
            IUnitOfWork uow,
            ITenantContext tenantContext,
            IClock clock,
            CancellationToken ct) =>
        {
            var tenantId = tenantContext.RequireTenantId();

            var jobId = await queue.EnqueueAsync(
                new PingJobPayload(request?.Message ?? "hello", clock.UtcNow),
                tenantId,
                idempotencyKey: request?.IdempotencyKey,
                ct: ct);

            // The enqueue joined this unit of work; nothing exists until this commits.
            await uow.SaveChangesAsync(ct);

            return Results.Accepted($"/api/diagnostics/jobs/{jobId}", new { jobId });
        })
        .WithSummary("Enqueues a ping job. Proves the transactional queue end to end.");

        group.MapGet("/jobs/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct);

            return job is null
                ? Results.NotFound()
                : Results.Ok(new JobResponse(
                    job.Id, job.Type, job.State.ToString(), job.Attempts, job.MaxAttempts,
                    job.RunAt, job.LeaseUntil, job.LockedBy, job.LastError, job.CompletedAt));
        })
        .WithSummary("Reads one job row.");

        group.MapGet("/jobs", async (AppDbContext db, string? state, CancellationToken ct) =>
        {
            var query = db.Jobs.AsNoTracking().AsQueryable();

            if (Enum.TryParse<JobState>(state, ignoreCase: true, out var parsed))
            {
                query = query.Where(j => j.State == parsed);
            }

            var jobs = await query
                .OrderByDescending(j => j.CreatedAt)
                .Take(50)
                .Select(j => new JobResponse(
                    j.Id, j.Type, j.State.ToString(), j.Attempts, j.MaxAttempts,
                    j.RunAt, j.LeaseUntil, j.LockedBy, j.LastError, j.CompletedAt))
                .ToListAsync(ct);

            return Results.Ok(jobs);
        })
        .WithSummary("Lists recent jobs, optionally filtered by state.");

        group.MapPost("/storage", async (
            IObjectStore store,
            ITenantContext tenantContext,
            IClock clock,
            CancellationToken ct) =>
        {
            var tenantId = tenantContext.RequireTenantId();
            var key = ObjectKey.ForTenant(tenantId, $"diagnostics/{clock.UtcNow:yyyyMMddHHmmssfff}.txt");
            var body = $"ContentPilot storage round trip at {clock.UtcNow:O}.";

            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(body)))
            {
                await store.PutAsync(key, stream, "text/plain; charset=utf-8", ct);
            }

            var url = await store.GetPresignedReadUrlAsync(key, TimeSpan.FromMinutes(15), ct);

            return Results.Ok(new { key = key.Value, url });
        })
        .WithSummary("Writes an object and returns a short-lived presigned URL.");

        return app;
    }
}

public sealed record PingRequest(string? Message, string? IdempotencyKey);

public sealed record JobResponse(
    Guid Id,
    string Type,
    string State,
    int Attempts,
    int MaxAttempts,
    DateTimeOffset RunAt,
    DateTimeOffset? LeaseUntil,
    string? LockedBy,
    string? LastError,
    DateTimeOffset? CompletedAt);
