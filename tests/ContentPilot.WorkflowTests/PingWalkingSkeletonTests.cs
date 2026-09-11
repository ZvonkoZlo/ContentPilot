using ContentPilot.Application.Abstractions;
using ContentPilot.Application.Jobs;
using ContentPilot.Domain.Tenancy;
using ContentPilot.Infrastructure;
using ContentPilot.TestSupport;
using ContentPilot.Infrastructure.Jobs;
using ContentPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ContentPilot.WorkflowTests;

/// <summary>
/// The Phase 0 acceptance test: a job enqueued by one process is executed exactly once by
/// a real dispatcher running two worker hosts against the same table, and the job row ends
/// up in a terminal state. Everything the orchestrator will need is proved here first.
/// </summary>
public sealed class PingWalkingSkeletonTests : IAsyncLifetime
{
    private PostgreSqlContainer? _postgres;

    public async Task InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            // The test itself is skipped by [DockerFact]; skip the container too.
            return;
        }

        _postgres = new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("contentpilot")
            .WithUsername("contentpilot")
            .WithPassword("contentpilot")
            .Build();

        await _postgres.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }

    [DockerFact]
    public async Task A_ping_job_is_executed_exactly_once_by_two_competing_workers()
    {
        var connectionString = _postgres!.GetConnectionString();

        using (var migrator = BuildHost(connectionString, withDispatcher: false))
        {
            await using var scope = migrator.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
        }

        Guid tenantId;
        Guid jobId;

        using (var producer = BuildHost(connectionString, withDispatcher: false))
        {
            await using var scope = producer.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var tenantContext = scope.ServiceProvider.GetRequiredService<IMutableTenantContext>();

            using (tenantContext.BeginCrossTenantScope())
            {
                var tenant = new Tenant("Appointso", "appointso");
                db.Tenants.Add(tenant);
                await db.SaveChangesAsync();
                tenantId = tenant.Id;
            }

            var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
            jobId = await queue.EnqueueAsync(new PingJobPayload("walking skeleton", DateTimeOffset.UtcNow), tenantId);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        PingJobHandler.Executed.Clear();

        // Two independent hosts, exactly as docker compose runs two worker replicas.
        var workerA = BuildHost(connectionString, withDispatcher: true);
        var workerB = BuildHost(connectionString, withDispatcher: true);

        await workerA.StartAsync();
        await workerB.StartAsync();

        try
        {
            await WaitForTerminalStateAsync(connectionString, jobId, TimeSpan.FromSeconds(45));
        }
        finally
        {
            await workerA.StopAsync();
            await workerB.StopAsync();
            workerA.Dispose();
            workerB.Dispose();
        }

        using var verifier = BuildHost(connectionString, withDispatcher: false);
        await using var verifyScope = verifier.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await verifyDb.Jobs.AsNoTracking().SingleAsync(j => j.Id == jobId);

        job.State.ShouldBe(JobState.Succeeded);
        job.Attempts.ShouldBe(1, "A second execution would mean the row lock failed.");
        job.CompletedAt.ShouldNotBeNull();
        job.LeaseUntil.ShouldBeNull();

        PingJobHandler.Executed.Count(id => id == jobId)
            .ShouldBe(1, "The handler ran more than once for a single job.");
    }

    private static async Task WaitForTerminalStateAsync(string connectionString, Guid jobId, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);

        using var host = BuildHost(connectionString, withDispatcher: false);

        while (!cts.IsCancellationRequested)
        {
            await using var scope = host.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var state = await db.Jobs.AsNoTracking().Where(j => j.Id == jobId).Select(j => j.State).FirstAsync(cts.Token);

            if (state is JobState.Succeeded or JobState.Dead or JobState.Cancelled)
            {
                return;
            }

            await Task.Delay(250, cts.Token);
        }

        throw new TimeoutException($"Job {jobId} did not reach a terminal state within {timeout}.");
    }

    private static IHost BuildHost(string connectionString, bool withDispatcher)
    {
        var builder = Host.CreateApplicationBuilder();

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = connectionString,
            ["ObjectStorage:Bucket"] = "unused-in-this-test",
            ["Jobs:PollInterval"] = "00:00:00.250",
            ["Jobs:Consumers"] = "2",
            ["Jobs:LeaseDuration"] = "00:00:30",

            // The dispatcher resolves every registered IJobHandler on every dispatch
            // attempt (see JobDispatcher.ExecuteAsync), so ContentItemWorkflowJobHandler's
            // dependency chain has to construct cleanly even in a host that only cares
            // about the ping job. ModelProfileRegistry refuses to construct with zero
            // profiles configured, so this is the minimum that satisfies it.
            ["Ai:Profiles:copywriter:ModelId"] = "unused-in-this-test",
        });

        builder.Logging.ClearProviders();
        builder.Services.AddContentPilotInfrastructure(builder.Configuration);

        if (withDispatcher)
        {
            builder.Services.AddContentPilotJobProcessing();
        }
        else
        {
            builder.Services.Configure<JobQueueOptions>(o => o.DispatcherEnabled = false);
        }

        return builder.Build();
    }
}
