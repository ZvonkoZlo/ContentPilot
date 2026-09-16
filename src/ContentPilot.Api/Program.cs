using ContentPilot.Api.Endpoints;
using ContentPilot.Infrastructure;
using ContentPilot.Infrastructure.Persistence;
using ContentPilot.Infrastructure.Telemetry;
using ContentPilot.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddContentPilotTelemetry("contentpilot-api");
builder.Services.AddContentPilotInfrastructure(builder.Configuration);

// The API enqueues work; it never consumes it. Keeping the dispatcher out of this
// process means a slow render can never starve HTTP request handling.
builder.Services.Configure<ContentPilot.Infrastructure.Jobs.JobQueueOptions>(o => o.DispatcherEnabled = false);

// Enums travel as names, in both directions. Numbers would make the API opaque, and
// without the converter a request body naming an enum value fails to bind at all.
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
});

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>("postgres");

// The minimal review UI (a separate Angular app, dev server on :4200 or :4400 depending on
// which port is free locally) has no cookie or session story yet — X-Tenant-Id is a plain
// header, not a credential — so a permissive dev-only CORS policy is enough; this never
// needs to widen once real auth exists, because that will replace the header with a bearer
// token instead of touching this policy.
builder.Services.AddCors(o => o.AddDefaultPolicy(policy => policy
    .WithOrigins("http://localhost:4200", "http://localhost:4400")
    .AllowAnyMethod()
    .AllowAnyHeader()));

var app = builder.Build();

// `--migrate` is an explicit init step, never automatic on startup: applying migrations
// from N replicas racing each other is how you corrupt a schema.
if (args.Contains("--migrate"))
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    app.Logger.LogInformation("Applying database migrations...");
    await db.Database.MigrateAsync();
    app.Logger.LogInformation("Migrations applied.");
    return;
}

// Explicit, like --migrate. Seeding on ordinary startup would quietly resurrect data an
// operator deleted on purpose.
if (args.Contains("--seed"))
{
    await using var scope = app.Services.CreateAsyncScope();
    var seeder = scope.ServiceProvider.GetRequiredService<ContentPilot.Infrastructure.Branding.GoldenTenantSeeder>();
    var seeded = await seeder.SeedAsync();
    app.Logger.LogInformation("Seeded tenant {TenantId}, brand {BrandId}.", seeded.TenantId, seeded.BrandId);
    return;
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseTenantResolution();

app.MapHealthChecks("/health");
app.MapTenantEndpoints();
app.MapBrandEndpoints();
app.MapBrandBrainEndpoints();
app.MapAssetEndpoints();
app.MapDiagnosticsEndpoints();
app.MapCampaignEndpoints();
app.MapAdminEndpoints();

app.Run();

/// <summary>Exposed so the integration tests can boot the real host.</summary>
public partial class Program;
