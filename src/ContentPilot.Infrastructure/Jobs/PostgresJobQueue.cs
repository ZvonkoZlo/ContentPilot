using System.Text.Json;
using ContentPilot.Application.Abstractions;
using ContentPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ContentPilot.Infrastructure.Jobs;

/// <summary>
/// Enlists a job in the caller's unit of work. It deliberately does not save: the point
/// of a database-backed queue is that the job and the state change it advances commit
/// in the same transaction.
/// </summary>
public sealed class PostgresJobQueue(AppDbContext db, IClock clock, IOptions<JobQueueOptions> options) : IJobQueue
{
    private readonly JobQueueOptions _options = options.Value;

    public async Task<Guid> EnqueueAsync<TPayload>(
        TPayload payload,
        Guid? tenantId = null,
        DateTimeOffset? runAt = null,
        string? idempotencyKey = null,
        JobPriority priority = JobPriority.Normal,
        CancellationToken ct = default)
        where TPayload : notnull
    {
        var type = JobTypeName.For<TPayload>();

        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            var key = idempotencyKey.Trim();

            // Cheap pre-check for the common case. The unique index is the real
            // guarantee; this only avoids a pointless insert attempt.
            var existing = await db.Jobs
                .AsNoTracking()
                .Where(j => j.IdempotencyKey == key)
                .Select(j => (Guid?)j.Id)
                .FirstOrDefaultAsync(ct);

            if (existing is not null)
            {
                return existing.Value;
            }

            var tracked = db.ChangeTracker.Entries<Job>()
                .FirstOrDefault(e => e.Entity.IdempotencyKey == key);

            if (tracked is not null)
            {
                return tracked.Entity.Id;
            }
        }

        var job = Job.Create(
            type,
            JsonSerializer.Serialize(payload, JobSerialization.Options),
            tenantId,
            clock.UtcNow,
            runAt,
            idempotencyKey,
            priority,
            _options.MaxAttempts);

        db.Jobs.Add(job);

        return job.Id;
    }
}

public sealed class JobQueueOptions
{
    public const string SectionName = "Jobs";

    /// <summary>Transient retries before a job is declared dead.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>How long one worker may hold a job before the reaper reclaims it.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Concurrent consumers per worker process.</summary>
    public int Consumers { get; set; } = 4;

    /// <summary>Idle sleep between empty polls. Kept short; the table is tiny.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Base for the exponential backoff applied to a failed attempt.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan ReaperInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Set false on the API so it only enqueues and never consumes.</summary>
    public bool DispatcherEnabled { get; set; } = true;

    public TimeSpan CalculateRetryDelay(int attempt)
    {
        var exponent = Math.Max(0, attempt - 1);
        var scaled = RetryBaseDelay * Math.Pow(2, Math.Min(exponent, 10));
        var capped = scaled > RetryMaxDelay ? RetryMaxDelay : scaled;

        // Jitter so a provider outage does not produce a synchronised retry stampede.
        var jitter = Random.Shared.NextDouble() * 0.3 + 0.85;

        return capped * jitter;
    }
}
