using ContentPilot.Application.Abstractions;
using ContentPilot.Domain.Branding;
using ContentPilot.Domain.Tenancy;
using ContentPilot.Infrastructure.Branding;
using ContentPilot.Infrastructure.Persistence;
using ContentPilot.IntegrationTests.Infrastructure;
using ContentPilot.TestSupport;
using ImageMagick;
using ImageMagick.Drawing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace ContentPilot.IntegrationTests;

/// <summary>
/// The one piece of SpecAssembly that genuinely needs Postgres and object storage: turning
/// an asset id into the bytes <c>SpecAssembler</c> is handed. Everything else about
/// SpecAssembly is pure and lives in the unit tests.
/// </summary>
[Collection(ContentPilotCollection.Name)]
public sealed class AssetContentResolverTests(ContentPilotFixture fixture)
{
    [DockerFact]
    public async Task An_uploaded_assets_bytes_round_trip_through_the_resolver()
    {
        var (_, brandId, scope) = await SeedAsync();
        await using var _scope = scope;

        var library = scope.ServiceProvider.GetRequiredService<AssetLibrary>();
        var resolver = scope.ServiceProvider.GetRequiredService<IAssetContentResolver>();

        var bytes = SamplePng(900, 1600);
        await using var content = new MemoryStream(bytes);
        var asset = await library.AddAsync(brandId, AssetKind.ProductScreenshot, content, "booking.png", "Booking screen", ["ui"]);

        var payload = await resolver.ResolveAsync(asset.Id);

        payload.MediaType.ShouldBe(asset.MediaType);
        payload.ToBytes().Length.ShouldBe((int)asset.Bytes);
    }

    [DockerFact]
    public async Task Resolving_several_slots_at_once_returns_one_entry_per_slot()
    {
        var (_, brandId, scope) = await SeedAsync();
        await using var _scope = scope;

        var library = scope.ServiceProvider.GetRequiredService<AssetLibrary>();
        var resolver = scope.ServiceProvider.GetRequiredService<IAssetContentResolver>();

        await using var screenshot = new MemoryStream(SamplePng(900, 1600));
        var screenshotAsset = await library.AddAsync(brandId, AssetKind.ProductScreenshot, screenshot, "booking.png", null, null);

        await using var logo = new MemoryStream(SamplePng(600, 160));
        var logoAsset = await library.AddAsync(brandId, AssetKind.Logo, logo, "logo.png", null, null);

        var resolved = await resolver.ResolveManyAsync(new Dictionary<string, Guid>
        {
            ["screenshot"] = screenshotAsset.Id,
            ["logo"] = logoAsset.Id,
        });

        resolved.Count.ShouldBe(2);
        resolved["screenshot"].ToBytes().Length.ShouldBe((int)screenshotAsset.Bytes);
        resolved["logo"].ToBytes().Length.ShouldBe((int)logoAsset.Bytes);
    }

    [DockerFact]
    public async Task An_unknown_asset_id_is_reported_by_id()
    {
        var (_, _, scope) = await SeedAsync();
        await using var _scope = scope;

        var resolver = scope.ServiceProvider.GetRequiredService<IAssetContentResolver>();
        var missing = Guid.CreateVersion7();

        var ex = await Should.ThrowAsync<BrandAssetNotFoundException>(() => resolver.ResolveAsync(missing));
        ex.AssetId.ShouldBe(missing);
    }

    [DockerFact]
    public async Task An_asset_belonging_to_another_tenant_does_not_resolve()
    {
        var (_, brandId, scope) = await SeedAsync();
        await using var _scope = scope;

        var library = scope.ServiceProvider.GetRequiredService<AssetLibrary>();
        await using var content = new MemoryStream(SamplePng(900, 1600));
        var asset = await library.AddAsync(brandId, AssetKind.ProductScreenshot, content, "booking.png", null, null);

        // A genuinely separate tenant, not another call to the (idempotent) golden seeder —
        // the query filter must make this asset id simply not exist from here, the same
        // guarantee BrandBrainReader relies on.
        var otherTenantId = await SeedUnrelatedTenantAsync();

        await using var otherScope = fixture.CreateScope();
        otherScope.ServiceProvider.GetRequiredService<IMutableTenantContext>().SetTenant(otherTenantId);
        var resolver = otherScope.ServiceProvider.GetRequiredService<IAssetContentResolver>();

        await Should.ThrowAsync<BrandAssetNotFoundException>(() => resolver.ResolveAsync(asset.Id));
    }

    private async Task<Guid> SeedUnrelatedTenantAsync()
    {
        await using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tenantContext = scope.ServiceProvider.GetRequiredService<IMutableTenantContext>();

        using var _ = tenantContext.BeginCrossTenantScope();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var tenant = new Tenant($"Unrelated {suffix}", $"unrelated-{suffix}");
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        return tenant.Id;
    }

    private async Task<(Guid TenantId, Guid BrandId, AsyncServiceScope Scope)> SeedAsync()
    {
        var scope = fixture.CreateScope();
        var seeder = scope.ServiceProvider.GetRequiredService<GoldenTenantSeeder>();
        var result = await seeder.SeedAsync();

        scope.ServiceProvider.GetRequiredService<IMutableTenantContext>().SetTenant(result.TenantId);

        return (result.TenantId, result.BrandId, scope);
    }

    private static byte[] SamplePng(uint width, uint height)
    {
        using var image = new MagickImage(MagickColors.White, width, height);

        new Drawables()
            .FillColor(new MagickColor("#6C4CF1")).Rectangle(0, 0, width, height / 8.0)
            .FillColor(new MagickColor("#F4F5F7")).RoundRectangle(20, height / 6.0, width - 20, height / 3.0, 12, 12)
            .Draw(image);

        image.Format = MagickFormat.Png;

        return image.ToByteArray();
    }
}
