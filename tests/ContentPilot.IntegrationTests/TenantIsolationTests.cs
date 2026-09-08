using ContentPilot.Application.Abstractions;
using ContentPilot.Domain.Branding;
using ContentPilot.Domain.Tenancy;
using ContentPilot.Infrastructure.Persistence;
using ContentPilot.IntegrationTests.Infrastructure;
using ContentPilot.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace ContentPilot.IntegrationTests;

/// <summary>
/// Isolation is the one thing that is far cheaper to prove now than to retrofit. Two real
/// tenants, real SQL, and an assertion that neither can see the other.
/// </summary>
[Collection(ContentPilotCollection.Name)]
public sealed class TenantIsolationTests(ContentPilotFixture fixture)
{
    [DockerFact]
    public async Task A_tenant_sees_only_its_own_brands()
    {
        var (tenantA, tenantB) = await SeedTwoTenantsAsync();

        await using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        scope.ServiceProvider.GetRequiredService<IMutableTenantContext>().SetTenant(tenantA);

        var visible = await db.Brands.AsNoTracking().ToListAsync();

        visible.ShouldHaveSingleItem();
        visible[0].TenantId.ShouldBe(tenantA);
        visible.ShouldNotContain(b => b.TenantId == tenantB);
    }

    [DockerFact]
    public async Task A_brand_from_another_tenant_is_invisible_even_by_its_own_identifier()
    {
        var (tenantA, tenantB) = await SeedTwoTenantsAsync();

        Guid brandOfB;

        await using (var seedScope = fixture.CreateScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            seedScope.ServiceProvider.GetRequiredService<IMutableTenantContext>().SetTenant(tenantB);
            brandOfB = (await seedDb.Brands.AsNoTracking().SingleAsync()).Id;
        }

        await using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        scope.ServiceProvider.GetRequiredService<IMutableTenantContext>().SetTenant(tenantA);

        // Filtered out of the query entirely: the API returns 404, so a probing caller
        // cannot even learn that the identifier exists.
        var found = await db.Brands.AsNoTracking().FirstOrDefaultAsync(b => b.Id == brandOfB);

        found.ShouldBeNull();
    }

    [DockerFact]
    public async Task Writing_a_row_into_another_tenant_is_refused()
    {
        var (tenantA, tenantB) = await SeedTwoTenantsAsync();

        await using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        scope.ServiceProvider.GetRequiredService<IMutableTenantContext>().SetTenant(tenantA);

        db.Brands.Add(new Brand(tenantB, "Smuggled", "UTC"));

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        ex.Message.ShouldContain("while tenant");
    }

    [DockerFact]
    public async Task A_cross_tenant_scope_sees_everything_and_then_closes_again()
    {
        var (tenantA, _) = await SeedTwoTenantsAsync();

        await using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tenantContext = scope.ServiceProvider.GetRequiredService<IMutableTenantContext>();
        tenantContext.SetTenant(tenantA);

        int all;

        using (tenantContext.BeginCrossTenantScope())
        {
            all = await db.Brands.AsNoTracking().CountAsync();
        }

        var scoped = await db.Brands.AsNoTracking().CountAsync();

        all.ShouldBeGreaterThanOrEqualTo(2);
        scoped.ShouldBe(1);
    }

    [DockerFact]
    public async Task Audit_timestamps_are_stamped_centrally()
    {
        var (tenantA, _) = await SeedTwoTenantsAsync();

        await using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        scope.ServiceProvider.GetRequiredService<IMutableTenantContext>().SetTenant(tenantA);

        var brand = await db.Brands.SingleAsync();
        brand.CreatedAt.ShouldNotBe(default);
        brand.UpdatedAt.ShouldBeNull();

        brand.Rename("Renamed");
        await db.SaveChangesAsync();

        brand.UpdatedAt.ShouldNotBeNull();
    }

    private async Task<(Guid TenantA, Guid TenantB)> SeedTwoTenantsAsync()
    {
        await using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tenantContext = scope.ServiceProvider.GetRequiredService<IMutableTenantContext>();

        using var _ = tenantContext.BeginCrossTenantScope();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var a = new Tenant($"Tenant A {suffix}", $"tenant-a-{suffix}");
        var b = new Tenant($"Tenant B {suffix}", $"tenant-b-{suffix}");

        db.Tenants.AddRange(a, b);
        db.Brands.Add(new Brand(a.Id, "Appointso", "Europe/Zagreb", ["hr", "en"]));
        db.Brands.Add(new Brand(b.Id, "Unrelated Bakery", "Europe/Berlin", ["de"]));

        await db.SaveChangesAsync();

        return (a.Id, b.Id);
    }
}
