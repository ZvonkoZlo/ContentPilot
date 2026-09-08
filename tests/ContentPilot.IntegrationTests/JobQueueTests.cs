using ContentPilot.Application.Abstractions;
using ContentPilot.Application.Jobs;
using ContentPilot.Domain.Tenancy;
using ContentPilot.Infrastructure.Jobs;
using ContentPilot.Infrastructure.Persistence;
using ContentPilot.IntegrationTests.Infrastructure;
using ContentPilot.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace ContentPilot.IntegrationTests;

/// <summary>
/// The properties the whole orchestrator will later depend on: a job is leased by exactly
/// one worker, an enqueue is transactional, and a duplicate trigger is absorbed.
/// </summary>
[Collection(ContentPilotCollection.Name)]
public sealed class JobQueueTests(ContentPilotFixture fixture)
{
    [DockerFact]
    public async Task An_enqueue_that_is_never_committed_leaves_no_job()
    {
        var tenantId = await SeedTenantAsync();
        Guid jobId;

        await using (var scope = fixture.CreateScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
            jobId = await queue.EnqueueAsync(new PingJobPayload("rolled back", DateTimeOffset.UtcNow), tenantId);
            // Deliberately no SaveChanges: this is the dual-write failure an external
            // broker would produce, and the reason the queue lives in Postgres.
        }

        await using var verify = fixture.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();

        (await db.Jobs.AnyAsync(j => j.Id == jobId)).ShouldBeFalse();
    }

    [DockerFact]
    public async Task A_committed_enqueue_is_pending_and_runnable()
    {
        var tenantId = await SeedTenantAsync();

        await using var scope = fixture.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var jobId = await queue.EnqueueAsync(new PingJobPayload("hello", DateTimeOffset.UtcNow), tenantId);
        await uow.SaveChangesAsync();

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == jobId);

        job.Type.ShouldBe("ping");
        job.State.ShouldBe(JobState.Pending);
        job.TenantId.ShouldBe(tenantId);
        job.Attempts.ShouldBe(0);
    }

    [DockerFact]
    public async Task The_same_idempotency_key_never_queues_twice()
    {
        var tenantId = await SeedTenantAsync();
        var key = $"weekly:{Guid.NewGuid():N}";

        var first = await EnqueueAsync(tenantId, key);
        var second = await EnqueueAsync(tenantId, key);

        // This is what makes a double-fired Monday cron harmless.
        second.ShouldBe(first);

        await using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        (await db.Jobs.CountAsync(j => j.IdempotencyKey == key)).ShouldBe(1);
    }

    [DockerFact]
    public async Task A_duplicate_key_committed_concurrently_loses_on_the_unique_index()
    {
        var tenantId = await SeedTenantAsync();
        var key = $"race:{Guid.NewGuid():N}";

        // Both scopes read "not present" before either commits, so only the database
        // constraint can settle it.
        var attempts = await Task.WhenAll(
            TryEnqueueAsync(tenantId, key),
            TryEnqueueAsync(tenantId, key));

        attempts.Count(ok => ok).ShouldBe(1, "Exactly one of two racing enqueues may win.");

        await using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.Jobs.CountAsync(j => j.IdempotencyKey == key)).ShouldBe(1);
    }

    [DockerFact]
    public async Task Two_workers_racing_for_one_job_produce_exactly_one_lease()
    {
        await ClearJobsAsync();
        var tenantId = await SeedTenantAsync();
        await EnqueueAsync(tenantId, idempotencyKey: null);

        // FOR UPDATE SKIP LOCKED is the entire concurrency design; if this ever regresses,
        // every campaign gets generated twice.
        var leases = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(i => LeaseOneAsync($"worker-{i}")));

        leases.Count(id => id is not null).ShouldBe(1);
    }

    [DockerFact]
    public async Task A_leased_job_is_no_longer_visible_to_other_workers()
    {
        await ClearJobsAsync();
        var tenantId = await SeedTenantAsync();
        await EnqueueAsync(tenantId, idempotencyKey: null);

        var first = await LeaseOneAsync("worker-a");
        var second = await LeaseOneAsync("worker-b");

        first.ShouldNotBeNull();
        second.ShouldBeNull();
    }

    [DockerFact]
    public async Task A_job_scheduled_in_the_future_is_not_leased_yet()
    {
        await ClearJobsAsync();
        var tenantId = await SeedTenantAsync();

        await using (var scope = fixture.CreateScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            await queue.EnqueueAsync(
                new PingJobPayload("later", DateTimeOffset.UtcNow),
                tenantId,
                runAt: DateTimeOffset.UtcNow.AddHours(1));

            await uow.SaveChangesAsync();
        }

        (await LeaseOneAsync("worker-early")).ShouldBeNull();
    }

    [DockerFact]
    public async Task High_priority_jobs_are_leased_first()
    {
        await ClearJobsAsync();
        var tenantId = await SeedTenantAsync();

        await using (var scope = fixture.CreateScope())
        {
            var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            await queue.EnqueueAsync(new PingJobPayload("low", DateTimeOffset.UtcNow), tenantId, priority: JobPriority.Low);
            await queue.EnqueueAsync(new PingJobPayload("high", DateTimeOffset.UtcNow), tenantId, priority: JobPriority.High);
            await uow.SaveChangesAsync();
        }

        var leasedId = await LeaseOneAsync("worker-priority");
        leasedId.ShouldNotBeNull();

        await using var verify = fixture.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == leasedId);

        // A user waiting on "Generate now" must not queue behind a scheduled batch.
        job.PayloadJson.ShouldContain("high");
    }

    /// <summary>
    /// Lease assertions count rows, so they need the queue empty. Tests in a collection
    /// run sequentially, which makes this safe and keeps each assertion exact.
    /// </summary>
    private async Task ClearJobsAsync()
    {
        await using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM jobs");
    }

    private async Task<Guid> EnqueueAsync(Guid tenantId, string? idempotencyKey)
    {
        await using var scope = fixture.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var id = await queue.EnqueueAsync(
            new PingJobPayload("hello", DateTimeOffset.UtcNow), tenantId, idempotencyKey: idempotencyKey);

        await uow.SaveChangesAsync();

        return id;
    }

    private async Task<bool> TryEnqueueAsync(Guid tenantId, string idempotencyKey)
    {
        try
        {
            await EnqueueAsync(tenantId, idempotencyKey);
            return true;
        }
        catch (DbUpdateException)
        {
            return false;
        }
    }

    /// <summary>
    /// Mirrors JobDispatcher.LeaseNextAsync exactly. Duplicated on purpose so the test
    /// asserts the SQL contract rather than reaching into a private method.
    /// </summary>
    private async Task<Guid?> LeaseOneAsync(string workerId)
    {
        await using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;

        await using var transaction = await db.Database.BeginTransactionAsync();

        var job = await db.Jobs
            .FromSql(
                $"""
                 SELECT * FROM jobs
                 WHERE state = 0 AND run_at <= {now}
                 ORDER BY priority, run_at
                 FOR UPDATE SKIP LOCKED
                 LIMIT 1
                 """)
            .FirstOrDefaultAsync();

        if (job is null)
        {
            await transaction.RollbackAsync();
            return null;
        }

        job.Lease(workerId, now, TimeSpan.FromMinutes(5));
        await db.SaveChangesAsync();
        await transaction.CommitAsync();

        return job.Id;
    }

    private async Task<Guid> SeedTenantAsync()
    {
        await using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tenantContext = scope.ServiceProvider.GetRequiredService<IMutableTenantContext>();

        using var _ = tenantContext.BeginCrossTenantScope();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var tenant = new Tenant($"Jobs {suffix}", $"jobs-{suffix}");
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        return tenant.Id;
    }
}
