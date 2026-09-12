using System.Diagnostics;
using ContentPilot.Application.Abstractions;
using ContentPilot.Infrastructure.Persistence;
using ContentPilot.Infrastructure.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ContentPilot.Infrastructure.Jobs;

/// <summary>
/// The worker loop. Leases one job at a time with FOR UPDATE SKIP LOCKED so any number
/// of worker processes can run against the same table without coordination, without a
/// broker, and without ever handing the same job to two consumers.
/// </summary>
public sealed class JobDispatcher(
    IServiceScopeFactory scopeFactory,
    IOptions<JobQueueOptions> options,
    ILogger<JobDispatcher> logger) : BackgroundService
{
    private readonly JobQueueOptions _options = options.Value;
    private readonly string _workerId = $"{Environment.MachineName}:{Environment.ProcessId}";

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.DispatcherEnabled)
        {
            logger.LogInformation("Job dispatcher is disabled in this process; jobs will be enqueued but not consumed.");
            return Task.CompletedTask;
        }

        logger.LogInformation(
            "Job dispatcher starting as {WorkerId} with {Consumers} consumers.", _workerId, _options.Consumers);

        var consumers = Enumerable
            .Range(0, Math.Max(1, _options.Consumers))
            .Select(i => Task.Run(() => ConsumeLoopAsync(i, stoppingToken), stoppingToken))
            .ToArray();

        return Task.WhenAll(consumers);
    }

    private async Task ConsumeLoopAsync(int consumerIndex, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var handled = await TryConsumeOneAsync(ct);

                if (!handled)
                {
                    await Task.Delay(_options.PollInterval, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A failure here is the loop itself misbehaving, not the job. Back off
                // rather than spinning hot against a database that is down.
                logger.LogError(ex, "Job consumer {ConsumerIndex} failed outside job execution.", consumerIndex);
                await Task.Delay(_options.PollInterval, ct);
            }
        }
    }

    private async Task<bool> TryConsumeOneAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        var leased = await LeaseNextAsync(db, clock, ct);

        if (leased is null)
        {
            return false;
        }

        using var activity = ContentPilotTelemetry.ActivitySource.StartActivity(
            $"job {leased.Type}", ActivityKind.Consumer);

        activity?.SetTag("job.id", leased.Id);
        activity?.SetTag("job.type", leased.Type);
        activity?.SetTag("job.attempt", leased.Attempts);
        activity?.SetTag("tenant.id", leased.TenantId);

        if (leased.TenantId is { } tenantId)
        {
            scope.ServiceProvider.GetRequiredService<IMutableTenantContext>().SetTenant(tenantId);
        }

        await ExecuteAsync(scope.ServiceProvider, db, clock, leased, activity, ct);

        return true;
    }

    /// <summary>
    /// Claims exactly one runnable job. The row lock is taken and released inside a short
    /// transaction; the work itself runs outside it, so a slow handler never holds a lock.
    /// </summary>
    private async Task<Job?> LeaseNextAsync(AppDbContext db, IClock clock, CancellationToken ct)
    {
        var now = clock.UtcNow;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var job = await db.Jobs
            .FromSql(
                $"""
                 SELECT * FROM jobs
                 WHERE state = 0 AND run_at <= {now}
                 ORDER BY priority, run_at
                 FOR UPDATE SKIP LOCKED
                 LIMIT 1
                 """)
            .FirstOrDefaultAsync(ct);

        if (job is null)
        {
            await transaction.RollbackAsync(ct);
            return null;
        }

        job.Lease(_workerId, now, _options.LeaseDuration);

        // Job is not tenant-owned, so no filter or guard applies here.
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return job;
    }

    private async Task ExecuteAsync(
        IServiceProvider services,
        AppDbContext db,
        IClock clock,
        Job job,
        Activity? activity,
        CancellationToken ct)
    {
        var handler = services.GetServices<IJobHandler>()
            .FirstOrDefault(h => string.Equals(h.JobType, job.Type, StringComparison.Ordinal));

        if (handler is null)
        {
            logger.LogError("No handler registered for job type {JobType} (job {JobId}).", job.Type, job.Id);
            await CompleteAsync(db, clock, job, error: $"No handler registered for job type '{job.Type}'.", permanent: true, ct);
            activity?.SetStatus(ActivityStatusCode.Error, "no handler");
            return;
        }

        var context = new JobExecutionContext(
            job.Id, job.Type, job.TenantId, job.Attempts, job.CreatedAt, clock.UtcNow)
        {
            ExtendLease = async (duration, token) =>
            {
                job.ExtendLease(clock.UtcNow, duration);
                await db.SaveChangesAsync(token);
            },
        };

        try
        {
            await handler.HandleAsync(context, job.PayloadJson, ct);
            await CompleteAsync(db, clock, job, error: null, permanent: false, ct);
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown, not failure. Leave the lease to expire so another worker picks
            // the job up rather than burning an attempt on a clean stop.
            logger.LogInformation("Job {JobId} abandoned for shutdown; its lease will be reclaimed.", job.Id);
            throw;
        }
        catch (PermanentJobFailureException ex)
        {
            logger.LogError(ex, "Job {JobId} ({JobType}) failed permanently.", job.Id, job.Type);
            DiscardPartialWork(db, job);
            await CompleteAsync(db, clock, job, ex.Message, permanent: true, ct);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "Job {JobId} ({JobType}) failed on attempt {Attempt} of {MaxAttempts}.",
                job.Id, job.Type, job.Attempts, job.MaxAttempts);

            DiscardPartialWork(db, job);
            await CompleteAsync(db, clock, job, $"{ex.GetType().Name}: {ex.Message}", permanent: false, ct);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        }
    }

    /// <summary>
    /// A handler shares this exact <c>AppDbContext</c> instance with the dispatcher (both
    /// come from the same job-execution DI scope) and often builds a multi-entity graph in
    /// several steps before its own <c>SaveChangesAsync</c> — items, then their workflow
    /// runs, then their jobs, say. If it throws partway through, those earlier steps are
    /// still sitting in the tracker as <c>Added</c>/<c>Modified</c> entries. Without this,
    /// <see cref="CompleteAsync"/>'s own save (recording the job's failure) would silently
    /// commit that half-built graph too — found live: a strategist plan with one item over
    /// a length limit crashed the fan-out after the first item was already <c>Added</c>,
    /// and the failure-path save alone was enough to leave that one item permanently
    /// orphaned, with no workflow run and no job ever created for it. Clearing the tracker
    /// and re-attaching only the job keeps a failed attempt's partial work exactly as
    /// uncommitted as a thrown exception is supposed to leave it.
    /// </summary>
    private static void DiscardPartialWork(AppDbContext db, Job job)
    {
        db.ChangeTracker.Clear();
        db.Jobs.Attach(job);
    }

    private async Task CompleteAsync(AppDbContext db, IClock clock, Job job, string? error, bool permanent, CancellationToken ct)
    {
        var now = clock.UtcNow;

        if (error is null)
        {
            job.Succeed(now);
        }
        else
        {
            job.Fail(now, error, _options.CalculateRetryDelay(job.Attempts), permanent);
        }

        // CancellationToken.None: the outcome must be recorded even during shutdown,
        // otherwise the job looks abandoned and gets retried for work already done.
        await db.SaveChangesAsync(CancellationToken.None);
    }
}
