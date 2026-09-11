using ContentPilot.Application.Abstractions;
using ContentPilot.Application.Jobs;
using ContentPilot.Infrastructure;
using ContentPilot.Infrastructure.Jobs;
using ContentPilot.Infrastructure.Persistence;
using ContentPilot.Infrastructure.Telemetry;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);

builder.AddContentPilotTelemetry("contentpilot-worker");
builder.Services.AddContentPilotInfrastructure(builder.Configuration);
builder.Services.AddContentPilotJobProcessing();

var host = builder.Build();

// The worker can also apply migrations, so a single-container deployment has one place
// to run them. Still explicit, never on ordinary startup.
if (args.Contains("--migrate"))
{
    await using var scope = host.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
    return;
}

// §21's scheduled trigger and daily reconciler are both self-rescheduling jobs (see
// CampaignTriggerScanJobHandler / CampaignTriggerReconcileJobHandler) — there is no
// separate scheduler process, only these two perpetually re-enqueueing themselves. That
// means exactly one of each has to exist at any time; this seeds the first occurrence,
// idempotently, so a restart never doubles them and a fresh database always gets one.
await using (var scope = host.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var jobQueue = scope.ServiceProvider.GetRequiredService<IJobQueue>();

    if (!await db.Jobs.AnyAsync(j => j.Type == JobTypeName.For<CampaignTriggerScanPayload>() && (j.State == JobState.Pending || j.State == JobState.Leased)))
    {
        await jobQueue.EnqueueAsync(new CampaignTriggerScanPayload(), tenantId: null);
    }

    if (!await db.Jobs.AnyAsync(j => j.Type == JobTypeName.For<CampaignTriggerReconcilePayload>() && (j.State == JobState.Pending || j.State == JobState.Leased)))
    {
        await jobQueue.EnqueueAsync(new CampaignTriggerReconcilePayload(), tenantId: null);
    }

    // §11's retention job is the same self-rescheduling shape as the two above.
    if (!await db.Jobs.AnyAsync(j => j.Type == JobTypeName.For<EnforceRetentionPayload>() && (j.State == JobState.Pending || j.State == JobState.Leased)))
    {
        await jobQueue.EnqueueAsync(new EnforceRetentionPayload(), tenantId: null);
    }

    await db.SaveChangesAsync();
}

await host.RunAsync();
