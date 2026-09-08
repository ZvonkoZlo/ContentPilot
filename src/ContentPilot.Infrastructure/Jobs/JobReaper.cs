using ContentPilot.Application.Abstractions;
using ContentPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ContentPilot.Infrastructure.Jobs;

/// <summary>
/// Recovers work abandoned by a worker that crashed, was killed mid-job, or lost its
/// database connection. Without it, a leased job is invisible forever — the classic
/// silent failure of a database-backed queue.
/// </summary>
public sealed class JobReaper(
    IServiceScopeFactory scopeFactory,
    IOptions<JobQueueOptions> options,
    ILogger<JobReaper> logger) : BackgroundService
{
    private readonly JobQueueOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.DispatcherEnabled)
        {
            return;
        }

        // Stagger the first pass so a fleet restart does not have every reaper scanning
        // in the same second.
        await Task.Delay(TimeSpan.FromSeconds(Random.Shared.Next(5, 20)), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var reclaimed = await ReapAsync(stoppingToken);

                if (reclaimed > 0)
                {
                    logger.LogWarning("Reclaimed {Count} job(s) whose lease had expired.", reclaimed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Job reaper pass failed.");
            }

            await Task.Delay(_options.ReaperInterval, stoppingToken);
        }
    }

    private async Task<int> ReapAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        var now = clock.UtcNow;

        var expired = await db.Jobs
            .Where(j => j.State == JobState.Leased && j.LeaseUntil != null && j.LeaseUntil < now)
            .OrderBy(j => j.LeaseUntil)
            .Take(100)
            .ToListAsync(ct);

        if (expired.Count == 0)
        {
            return 0;
        }

        foreach (var job in expired)
        {
            job.ReclaimExpiredLease(now, _options.CalculateRetryDelay(job.Attempts));
        }

        await db.SaveChangesAsync(ct);

        return expired.Count;
    }
}
