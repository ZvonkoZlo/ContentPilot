using System.Net;
using System.Net.Http.Json;
using ContentPilot.Application.Abstractions;
using ContentPilot.Domain.Content;
using ContentPilot.Domain.Tenancy;
using ContentPilot.Domain.Workflow;
using ContentPilot.Infrastructure.Jobs;
using ContentPilot.Infrastructure.Persistence;
using ContentPilot.IntegrationTests.Infrastructure;
using ContentPilot.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace ContentPilot.IntegrationTests;

/// <summary>
/// Phase 9's operator visibility, over real HTTP: dead jobs and stuck runs, cross-tenant by
/// design — an operator's job is to see across every tenant at once, so these are the one
/// place in the API that deliberately does not scope to a single caller's tenant.
/// </summary>
[Collection(ContentPilotCollection.Name)]
public sealed class AdminEndpointTests(ContentPilotFixture fixture) : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private HttpClient _client = default!;

    public Task InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            return Task.CompletedTask;
        }

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(fixture.Settings));
        });

        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        return Task.CompletedTask;
    }

    [DockerFact]
    public async Task A_permanently_failed_job_shows_up_as_dead()
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var job = Job.Create("admin-test-job", "{}", null, DateTimeOffset.UtcNow, null, null, JobPriority.Normal, maxAttempts: 1);
        job.Lease("test-worker", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
        job.Fail(DateTimeOffset.UtcNow, "Simulated permanent failure for the admin view test.", TimeSpan.Zero, permanent: true);
        db.Jobs.Add(job);
        await db.SaveChangesAsync();

        var response = await _client.GetAsync("/api/admin/dead-jobs?limit=500");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var jobs = await response.Content.ReadFromJsonAsync<List<DeadJobDto>>();
        jobs!.ShouldContain(j => j.Id == job.Id && j.Type == "admin-test-job" && j.LastError!.Contains("Simulated permanent failure"));
    }

    [DockerFact]
    public async Task A_run_past_its_own_deadline_shows_up_as_stuck()
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tenantContext = scope.ServiceProvider.GetRequiredService<IMutableTenantContext>();

        Guid tenantId, campaignId, runId;

        using (tenantContext.BeginCrossTenantScope())
        {
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var tenant = new Tenant($"Admin test {suffix}", $"admin-test-{suffix}");
            db.Tenants.Add(tenant);

            var brand = new Domain.Branding.Brand(tenant.Id, "Admin Brand", "UTC");
            db.Brands.Add(brand);
            await db.SaveChangesAsync();

            var campaign = new ContentCampaign(tenant.Id, brand.Id, new DateOnly(2036, 8, 3), CampaignTrigger.Manual, Guid.CreateVersion7(), 200_000_000);
            db.ContentCampaigns.Add(campaign);
            await db.SaveChangesAsync();

            // Started a day ago with the default (45-minute) run duration — its deadline is
            // long past, and nothing has marked it terminal, which is exactly "stuck".
            var run = new WorkflowRun(tenant.Id, campaign.Id, WorkflowScope.Campaign, campaign.Id, DateTimeOffset.UtcNow.AddDays(-1));
            db.WorkflowRuns.Add(run);
            await db.SaveChangesAsync();

            tenantId = tenant.Id;
            campaignId = campaign.Id;
            runId = run.Id;
        }

        var response = await _client.GetAsync("/api/admin/stuck-runs");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var runs = await response.Content.ReadFromJsonAsync<List<StuckRunDto>>();
        runs!.ShouldContain(r => r.Id == runId && r.TenantId == tenantId && r.CampaignId == campaignId);
    }

    private sealed record DeadJobDto(Guid Id, string Type, Guid? TenantId, int Attempts, int MaxAttempts, string? LastError, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt);

    private sealed record StuckRunDto(Guid Id, Guid TenantId, string Scope, Guid CampaignId, Guid EntityId, DateTimeOffset Deadline, DateTimeOffset? LeaseUntil, string? LeaseOwner, int StepsExecuted);
}
