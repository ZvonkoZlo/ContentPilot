using System.Text;
using ContentPilot.Application.Abstractions;
using ContentPilot.Application.Brand;
using ContentPilot.Application.Capabilities;
using ContentPilot.Domain.Branding;
using ContentPilot.Infrastructure.Branding;
using ContentPilot.Infrastructure.Persistence;
using ContentPilot.IntegrationTests.Infrastructure;
using ContentPilot.TestSupport;
using ImageMagick;
using ImageMagick.Drawing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace ContentPilot.IntegrationTests;

/// <summary>
/// The Brand Brain against real Postgres and real MinIO: jsonb round trips, content
/// addressing, version reuse, and the prompt block an agent will actually be handed.
/// </summary>
[Collection(ContentPilotCollection.Name)]
public sealed class BrandBrainTests(ContentPilotFixture fixture)
{
    [DockerFact]
    public async Task The_golden_tenant_seeds_into_a_brain_with_no_missing_pieces()
    {
        var (_, brandId, scope) = await SeedAsync();
        await using var _scope = scope;

        var reader = scope.ServiceProvider.GetRequiredService<IBrandBrainReader>();
        var snapshot = await reader.GetSnapshotAsync(brandId);

        snapshot.Name.ShouldBe("Appointso");
        snapshot.Facts.Count.ShouldBeGreaterThanOrEqualTo(10);
        snapshot.Personas.ShouldContain(p => p.IsPrimary);
        snapshot.Quota.Total.ShouldBe(4);
        snapshot.Voice.BannedWords.ShouldContain("revolutionary");

        // Only the asset warnings should remain: the seeder brings no binaries with it.
        var warnings = BrandBrainAssembler.Diagnose(snapshot);
        warnings.ShouldAllBe(w => w.Contains("screenshot") || w.Contains("logo"));
    }

    [DockerFact]
    public async Task Seeding_twice_changes_nothing()
    {
        var (_, brandId, scope) = await SeedAsync();
        await using var _scope = scope;

        var seeder = scope.ServiceProvider.GetRequiredService<GoldenTenantSeeder>();
        await seeder.SeedAsync();

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        using (scope.ServiceProvider.GetRequiredService<IMutableTenantContext>().BeginCrossTenantScope())
        {
            (await db.ProductFacts.CountAsync(f => f.BrandId == brandId && f.Key == "whatsapp-reminders")).ShouldBe(1);
            (await db.AudiencePersonas.CountAsync(p => p.BrandId == brandId && p.IsPrimary)).ShouldBe(1);
        }
    }

    [DockerFact]
    public async Task Jsonb_columns_round_trip_with_their_collections_intact()
    {
        var (_, brandId, scope) = await SeedAsync();
        await using var _scope = scope;

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var profile = await db.BrandProfiles.AsNoTracking().SingleAsync(p => p.BrandId == brandId);

        profile.Visual.StyleKeywords.ShouldContain("clean");
        profile.Voice.ForbiddenClaims.ShouldNotBeEmpty();
        profile.Messaging.Principles.ShouldNotBeEmpty();
        profile.Visual.PrimaryColor.ShouldBe("#6C4CF1");
    }

    [DockerFact]
    public async Task An_unchanged_brand_reuses_its_version()
    {
        var (_, brandId, scope) = await SeedAsync();
        await using var _scope = scope;

        var reader = scope.ServiceProvider.GetRequiredService<IBrandBrainReader>();

        var first = await reader.CaptureVersionAsync(brandId);
        var second = await reader.CaptureVersionAsync(brandId);

        first.WasCreated.ShouldBeTrue();

        // Otherwise the version history records how often we ran rather than what changed.
        second.WasCreated.ShouldBeFalse();
        second.VersionId.ShouldBe(first.VersionId);
    }

    [DockerFact]
    public async Task Editing_the_brand_creates_a_new_version_and_leaves_the_old_one_intact()
    {
        var (_, brandId, scope) = await SeedAsync();
        await using var _scope = scope;

        var reader = scope.ServiceProvider.GetRequiredService<IBrandBrainReader>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var before = await reader.CaptureVersionAsync(brandId);

        var profile = await db.BrandProfiles.SingleAsync(p => p.BrandId == brandId);
        profile.UpdateMessaging(profile.Messaging with { CorePromise = "A completely different promise." });
        await db.SaveChangesAsync();

        var after = await reader.CaptureVersionAsync(brandId);

        after.WasCreated.ShouldBeTrue();
        after.VersionId.ShouldNotBe(before.VersionId);

        // The whole point of snapshotting: what a past campaign saw is still readable, and
        // is not retroactively rewritten by an edit.
        var restored = await reader.GetVersionAsync(before.VersionId);
        restored.Messaging.CorePromise.ShouldBe("Stop losing bookings to unanswered messages.");
    }

    [DockerFact]
    public async Task An_uploaded_asset_is_stored_measured_and_downloadable()
    {
        var (_, brandId, scope) = await SeedAsync();
        await using var _scope = scope;

        var library = scope.ServiceProvider.GetRequiredService<AssetLibrary>();

        await using var content = new MemoryStream(SamplePng(900, 1600));
        var asset = await library.AddAsync(brandId, AssetKind.ProductScreenshot, content, "booking.png", "Booking screen", ["ui"]);

        asset.Width.ShouldBe(900);
        asset.Height.ShouldBe(1600);
        asset.Origin.ShouldBe(AssetOrigin.Upload);
        asset.DominantColors.ShouldNotBeEmpty();
        asset.StorageKey.ShouldContain(asset.Sha256);

        var url = await library.GetDownloadUrlAsync(asset.Id, TimeSpan.FromMinutes(5));

        using var http = new HttpClient();
        (await http.GetAsync(url)).IsSuccessStatusCode.ShouldBeTrue();
    }

    [DockerFact]
    public async Task The_same_file_uploaded_twice_becomes_one_asset()
    {
        var (_, brandId, scope) = await SeedAsync();
        await using var _scope = scope;

        var library = scope.ServiceProvider.GetRequiredService<AssetLibrary>();
        var bytes = SamplePng(800, 1200);

        await using var first = new MemoryStream(bytes);
        var a = await library.AddAsync(brandId, AssetKind.ProductScreenshot, first, "calendar.png", null, null);

        await using var second = new MemoryStream(bytes);
        var b = await library.AddAsync(brandId, AssetKind.ProductScreenshot, second, "calendar-copy.png", null, null);

        // Content addressing: the same picture under a different name is the same asset.
        b.Id.ShouldBe(a.Id);
    }

    [DockerFact]
    public async Task A_thumbnail_is_derived_and_kept_out_of_the_library_listing()
    {
        var (_, brandId, scope) = await SeedAsync();
        await using var _scope = scope;

        var library = scope.ServiceProvider.GetRequiredService<AssetLibrary>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await using var content = new MemoryStream(SamplePng(1200, 1600));
        var asset = await library.AddAsync(brandId, AssetKind.ProductScreenshot, content, "wide.png", null, null);

        var thumb = await db.BrandAssets.AsNoTracking()
            .FirstOrDefaultAsync(a => a.ParentAssetId == asset.Id && a.Variant == "thumb");

        thumb.ShouldNotBeNull();
        thumb.Width.ShouldBeLessThan(asset.Width);

        // Derived rows are an implementation detail; the creative director must not see one.
        var reader = scope.ServiceProvider.GetRequiredService<IBrandBrainReader>();
        var snapshot = await reader.GetSnapshotAsync(brandId);

        snapshot.Assets.ShouldNotContain(a => a.Id == thumb.Id);
        snapshot.Assets.ShouldContain(a => a.Id == asset.Id);
    }

    [DockerFact]
    public async Task A_hostile_upload_never_reaches_storage()
    {
        var (_, brandId, scope) = await SeedAsync();
        await using var _scope = scope;

        var library = scope.ServiceProvider.GetRequiredService<AssetLibrary>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var before = await db.BrandAssets.CountAsync(a => a.BrandId == brandId);

        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("MZ this is not an image at all, really"));

        await Should.ThrowAsync<UnsupportedAssetException>(
            () => library.AddAsync(brandId, AssetKind.Logo, content, "logo.png", null, null));

        (await db.BrandAssets.CountAsync(a => a.BrandId == brandId)).ShouldBe(before);
    }

    [DockerFact]
    public async Task The_prompt_block_reads_as_a_brief_and_fits_its_budget()
    {
        var (_, brandId, scope) = await SeedAsync();
        await using var _scope = scope;

        var reader = scope.ServiceProvider.GetRequiredService<IBrandBrainReader>();
        var block = BrandBlockRenderer.Render(await reader.GetSnapshotAsync(brandId));

        block.ShouldContain("Appointso");
        block.ShouldContain("`whatsapp-reminders`");
        block.ShouldContain("(primary)");
        block.ShouldContain("Never make these claims");

        BrandBlockRenderer.EstimateTokens(block).ShouldBeLessThan(BrandBlockRenderer.DefaultTokenBudget);
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
            .FillColor(new MagickColor("#1B1F27")).Ellipse(width / 2.0, height / 2.0, width / 6.0, height / 12.0, 0, 360)
            .Draw(image);

        image.Format = MagickFormat.Png;

        return image.ToByteArray();
    }
}
