using ContentPilot.Infrastructure;
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

await host.RunAsync();
