using ContentPilot.Application.Abstractions;
using ContentPilot.Application.Campaigns;
using ContentPilot.Application.Jobs;
using ContentPilot.Domain.Branding;
using ContentPilot.Domain.Content;
using ContentPilot.Domain.Tenancy;
using ContentPilot.Infrastructure.Campaigns;
using ContentPilot.Infrastructure.Jobs;
using ContentPilot.Infrastructure.Persistence;
using ContentPilot.IntegrationTests.Infrastructure;
using ContentPilot.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace ContentPilot.IntegrationTests;

/// <summary>
/// §21's two scheduled paths, against real Postgres: the hourly scan that fires only in a
/// brand's own 06:00-Monday window, and the daily reconciler that starts a campaign for any
/// active brand that has none for the current week regardless of the hour. Both call
/// <see cref="CampaignStarter"/> — the same code the manual trigger endpoint runs — so what
/// is actually new here is the timing logic, not a second way to start a campaign.
/// <para>
/// The brand here is timezone <c>"UTC"</c> rather than a real IANA zone such as the golden
/// tenant's "Europe/Zagreb" — deliberately, so the test does not depend on OS timezone data
/// the build's <c>InvariantGlobalization</c> setting can leave unavailable in some dev
/// environments (Linux resolves IANA ids natively regardless; some Windows setups do not).
/// "UTC" is a BCL-guaranteed id everywhere, and the handler's own timezone-lookup code path
/// is exercised identically either way.
/// </para>
/// <para>
/// Both handlers genuinely iterate every active brand in the database, including ones other
/// tests created — that is the correct production behaviour, not a test leak — so every
/// assertion below is scoped to the seeded brand's own id rather than assuming it is the
/// only campaign a pass produced.
/// </para>
/// </summary>
[Collection(ContentPilotCollection.Name)]
public sealed class CampaignTriggerJobHandlerTests(ContentPilotFixture fixture)
{
    // Far from every other test's campaign weeks. 2035-01-01 is a real Monday.
    private static readonly DateOnly TargetMonday = new(2035, 1, 1);

    private static DateTimeOffset At(DateOnly date, int hour) =>
        new(date.Year, date.Month, date.Day, hour, 0, 0, TimeSpan.Zero);

    [DockerFact]
    public async Task The_scan_starts_a_campaign_exactly_in_the_brands_own_trigger_window()
    {
        var (brandId, scope) = await SeedAsync();
        await using var _scope = scope;

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var handler = BuildScanHandler(scope.ServiceProvider, At(TargetMonday, hour: 6));

        await ((IJobHandler)handler).HandleAsync(Context(handler), "{}", CancellationToken.None);

        var campaign = await db.ContentCampaigns.AsNoTracking()
            .SingleOrDefaultAsync(c => c.BrandId == brandId && c.WeekStart == TargetMonday);

        campaign.ShouldNotBeNull();
        campaign.Trigger.ShouldBe(CampaignTrigger.Scheduled);
    }

    [DockerFact]
    public async Task The_scan_does_nothing_outside_the_trigger_window()
    {
        var (brandId, scope) = await SeedAsync();
        await using var _scope = scope;

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Same Monday, three hours later than the trigger hour — the common case, since
        // this handler fires roughly hourly and only one of those firings should ever act.
        var handler = BuildScanHandler(scope.ServiceProvider, At(TargetMonday, hour: 9));

        await ((IJobHandler)handler).HandleAsync(Context(handler), "{}", CancellationToken.None);

        (await db.ContentCampaigns.CountAsync(c => c.BrandId == brandId && c.WeekStart == TargetMonday)).ShouldBe(0);
    }

    [DockerFact]
    public async Task The_reconciler_catches_a_week_the_scan_missed_regardless_of_the_hour()
    {
        var (brandId, scope) = await SeedAsync();
        await using var _scope = scope;

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Wednesday afternoon — nowhere near the scan's own trigger window — proving the
        // reconciler's whole point: it does not wait for the exact hour to notice a gap.
        var handler = BuildReconcileHandler(scope.ServiceProvider, At(TargetMonday.AddDays(2), hour: 15));

        await ((IJobHandler)handler).HandleAsync(Context(handler), "{}", CancellationToken.None);

        var campaign = await db.ContentCampaigns.AsNoTracking()
            .SingleOrDefaultAsync(c => c.BrandId == brandId && c.WeekStart == TargetMonday);

        campaign.ShouldNotBeNull();
        campaign.Trigger.ShouldBe(CampaignTrigger.Scheduled);
    }

    [DockerFact]
    public async Task The_reconciler_leaves_a_week_that_already_has_a_campaign_alone()
    {
        var (brandId, scope) = await SeedAsync();
        await using var _scope = scope;

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var scan = BuildScanHandler(scope.ServiceProvider, At(TargetMonday, hour: 6));
        await ((IJobHandler)scan).HandleAsync(Context(scan), "{}", CancellationToken.None);

        var reconcile = BuildReconcileHandler(scope.ServiceProvider, At(TargetMonday, hour: 20));
        await ((IJobHandler)reconcile).HandleAsync(Context(reconcile), "{}", CancellationToken.None);

        (await db.ContentCampaigns.CountAsync(c => c.BrandId == brandId && c.WeekStart == TargetMonday)).ShouldBe(1);
    }

    private static JobExecutionContext Context(IJobHandler handler) =>
        new(Guid.CreateVersion7(), handler.JobType, null, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private async Task<(Guid BrandId, AsyncServiceScope Scope)> SeedAsync()
    {
        var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tenantContext = scope.ServiceProvider.GetRequiredService<IMutableTenantContext>();

        Guid brandId;

        using (tenantContext.BeginCrossTenantScope())
        {
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var tenant = new Tenant($"Trigger test {suffix}", $"trigger-test-{suffix}");
            db.Tenants.Add(tenant);

            var brand = new Brand(tenant.Id, "UTC Brand", "UTC");
            db.Brands.Add(brand);

            await db.SaveChangesAsync();
            brandId = brand.Id;
        }

        return (brandId, scope);
    }

    private static CampaignTriggerScanJobHandler BuildScanHandler(IServiceProvider services, DateTimeOffset now) => new(
        services.GetRequiredService<AppDbContext>(),
        services.GetRequiredService<IJobQueue>(),
        new FixedClock(now),
        services.GetRequiredService<IMutableTenantContext>(),
        BuildStarter(services),
        NullLogger<CampaignTriggerScanJobHandler>.Instance);

    private static CampaignTriggerReconcileJobHandler BuildReconcileHandler(IServiceProvider services, DateTimeOffset now) => new(
        services.GetRequiredService<AppDbContext>(),
        services.GetRequiredService<IJobQueue>(),
        new FixedClock(now),
        services.GetRequiredService<IMutableTenantContext>(),
        BuildStarter(services),
        NullLogger<CampaignTriggerReconcileJobHandler>.Instance);

    private static CampaignStarter BuildStarter(IServiceProvider services) => new(
        services.GetRequiredService<AppDbContext>(),
        services.GetRequiredService<Application.Capabilities.IBrandBrainReader>(),
        services.GetRequiredService<IJobQueue>());

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
