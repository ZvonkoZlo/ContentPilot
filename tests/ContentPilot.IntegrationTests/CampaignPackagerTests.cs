using System.Text;
using System.Text.Json;
using ContentPilot.Application.Abstractions;
using ContentPilot.Application.Agents;
using ContentPilot.Application.Capabilities;
using ContentPilot.Domain.Content;
using ContentPilot.Domain.Tenancy;
using ContentPilot.Domain.Workflow;
using ContentPilot.Infrastructure.Branding;
using ContentPilot.Infrastructure.Packaging;
using ContentPilot.Infrastructure.Persistence;
using ContentPilot.IntegrationTests.Infrastructure;
using ContentPilot.TestSupport;
using ImageMagick;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace ContentPilot.IntegrationTests;

/// <summary>
/// Phase 8's packaging step against real Postgres and MinIO. The item pipeline itself
/// (rendering, QA, approval) is <see cref="ContentItemWorkflowJobHandlerTests"/>'s job;
/// here an item is set up already at the point packaging cares about — an approved status,
/// a real asset in object storage, and a Writing step's copy — so the test stays about what
/// <see cref="CampaignPackager"/> itself does: build <c>plan.json</c>, <c>manifest.json</c>,
/// and lay every file out where the manifest says it is.
/// </summary>
[Collection(ContentPilotCollection.Name)]
public sealed class CampaignPackagerTests(ContentPilotFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 6, 0, 0, TimeSpan.Zero);
    private static int _week;

    [DockerFact]
    public async Task Building_a_package_writes_plan_manifest_and_every_items_files()
    {
        var (campaign, _, scope) = await SetupApprovedItemAsync();
        await using var _scope = scope;

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<IObjectStore>();
        var packager = scope.ServiceProvider.GetRequiredService<CampaignPackager>();

        var package = await packager.BuildAsync(campaign, default);
        await db.SaveChangesAsync();

        var planKey = ObjectKey.ForTenant(campaign.TenantId, $"campaigns/{campaign.Id:N}/plan.json");
        var manifestKey = ObjectKey.ForTenant(campaign.TenantId, $"campaigns/{campaign.Id:N}/manifest.json");
        var imageKey = ObjectKey.ForTenant(campaign.TenantId, $"campaigns/{campaign.Id:N}/post-01/image.png");
        var captionKey = ObjectKey.ForTenant(campaign.TenantId, $"campaigns/{campaign.Id:N}/post-01/caption.txt");
        var metadataKey = ObjectKey.ForTenant(campaign.TenantId, $"campaigns/{campaign.Id:N}/post-01/metadata.json");

        (await store.ExistsAsync(planKey)).ShouldBeTrue();
        (await store.ExistsAsync(manifestKey)).ShouldBeTrue();
        (await store.ExistsAsync(imageKey)).ShouldBeTrue();
        (await store.ExistsAsync(captionKey)).ShouldBeTrue();
        (await store.ExistsAsync(metadataKey)).ShouldBeTrue();

        await using var captionStream = await store.GetAsync(captionKey);
        using var captionReader = new StreamReader(captionStream);
        (await captionReader.ReadToEndAsync()).ShouldContain("Fill the empty slots in your week");

        var manifest = JsonSerializer.Deserialize<JsonElement>(package.ManifestJson);
        var files = manifest.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("path").GetString()).ToList();

        files.ShouldContain("post-01/image.png");
        files.ShouldContain("post-01/caption.txt");
        files.ShouldContain("post-01/metadata.json");

        var stored = await db.CampaignPackages.AsNoTracking().SingleAsync(p => p.CampaignId == campaign.Id);
        stored.Id.ShouldBe(package.Id);
    }

    [DockerFact]
    public async Task Building_the_same_campaign_twice_rebuilds_the_one_row_rather_than_duplicating_it()
    {
        var (campaign, _, scope) = await SetupApprovedItemAsync();
        await using var _scope = scope;

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var packager = scope.ServiceProvider.GetRequiredService<CampaignPackager>();

        var first = await packager.BuildAsync(campaign, default);
        await db.SaveChangesAsync();

        var second = await packager.BuildAsync(campaign, default);
        await db.SaveChangesAsync();

        second.Id.ShouldBe(first.Id);
        (await db.CampaignPackages.CountAsync(p => p.CampaignId == campaign.Id)).ShouldBe(1);
    }

    [DockerFact]
    public async Task An_item_that_never_rendered_anything_is_listed_with_no_folder()
    {
        var weekStart = new DateOnly(2036, 1, 7).AddDays(7 * Interlocked.Increment(ref _week));
        var scope = fixture.CreateScope();
        var seeder = scope.ServiceProvider.GetRequiredService<GoldenTenantSeeder>();
        var seeded = await seeder.SeedAsync();
        scope.ServiceProvider.GetRequiredService<IMutableTenantContext>().SetTenant(seeded.TenantId);

        var brandReader = scope.ServiceProvider.GetRequiredService<IBrandBrainReader>();
        var version = await brandReader.CaptureVersionAsync(seeded.BrandId);

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var campaign = new ContentCampaign(seeded.TenantId, seeded.BrandId, weekStart, CampaignTrigger.Manual, version.VersionId, 200_000_000);
        db.ContentCampaigns.Add(campaign);

        var item = new ContentItem(
            seeded.TenantId, campaign.Id, ContentItemType.StaticPost,
            "Never got past Directing", "problem-solution", "n/a", DayOfWeek.Monday, 1);
        item.SendToHumanReview("No eligible template.");
        db.ContentItems.Add(item);
        await db.SaveChangesAsync();

        var packager = scope.ServiceProvider.GetRequiredService<CampaignPackager>();
        var package = await packager.BuildAsync(campaign, default);
        await db.SaveChangesAsync();

        var manifest = JsonSerializer.Deserialize<JsonElement>(package.ManifestJson);
        // plan.json and manifest.json describe themselves nowhere — an unrendered item
        // contributes no per-item files, so the manifest's own file list is empty.
        manifest.GetProperty("files").GetArrayLength().ShouldBe(0);

        await using var _scope = scope;
    }

    [DockerFact]
    public async Task The_zip_contains_plan_manifest_and_every_items_files()
    {
        var (campaign, _, scope) = await SetupApprovedItemAsync();
        await using var _scope = scope;

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<IObjectStore>();
        var packager = scope.ServiceProvider.GetRequiredService<CampaignPackager>();
        var zipBuilder = scope.ServiceProvider.GetRequiredService<CampaignZipBuilder>();

        var package = await packager.BuildAsync(campaign, default);
        await db.SaveChangesAsync();

        var zipKey = await zipBuilder.BuildOrGetAsync(campaign, package, default);
        await db.SaveChangesAsync();

        await using var zipStream = await store.GetAsync(zipKey);
        using var buffer = new MemoryStream();
        await zipStream.CopyToAsync(buffer);
        buffer.Position = 0;

        using var archive = new System.IO.Compression.ZipArchive(buffer, System.IO.Compression.ZipArchiveMode.Read);
        var entries = archive.Entries.Select(e => e.FullName).ToList();

        entries.ShouldContain("plan.json");
        entries.ShouldContain("manifest.json");
        entries.ShouldContain("post-01/image.png");
        entries.ShouldContain("post-01/caption.txt");
        entries.ShouldContain("post-01/metadata.json");

        var reloaded = await db.CampaignPackages.AsNoTracking().SingleAsync(p => p.CampaignId == campaign.Id);
        reloaded.ZipKey.ShouldBe(zipKey.Value);
    }

    [DockerFact]
    public async Task Building_the_zip_twice_reuses_the_same_object_rather_than_rebuilding()
    {
        var (campaign, _, scope) = await SetupApprovedItemAsync();
        await using var _scope = scope;

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var packager = scope.ServiceProvider.GetRequiredService<CampaignPackager>();
        var zipBuilder = scope.ServiceProvider.GetRequiredService<CampaignZipBuilder>();

        var package = await packager.BuildAsync(campaign, default);
        await db.SaveChangesAsync();

        var first = await zipBuilder.BuildOrGetAsync(campaign, package, default);
        await db.SaveChangesAsync();

        // Re-load the same package row (a fresh read, as a second request would) rather than
        // reuse the in-memory instance, so this proves the cache survives a round trip.
        var reloaded = await db.CampaignPackages.SingleAsync(p => p.CampaignId == campaign.Id);
        var second = await zipBuilder.BuildOrGetAsync(campaign, reloaded, default);

        second.ShouldBe(first);
    }

    [DockerFact]
    public async Task Rebuilding_the_package_clears_the_cached_zip_so_a_stale_one_is_never_served()
    {
        var (campaign, _, scope) = await SetupApprovedItemAsync();
        await using var _scope = scope;

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var packager = scope.ServiceProvider.GetRequiredService<CampaignPackager>();
        var zipBuilder = scope.ServiceProvider.GetRequiredService<CampaignZipBuilder>();

        var package = await packager.BuildAsync(campaign, default);
        await db.SaveChangesAsync();
        await zipBuilder.BuildOrGetAsync(campaign, package, default);
        await db.SaveChangesAsync();

        package.ZipKey.ShouldNotBeNull();

        var rebuilt = await packager.BuildAsync(campaign, default);
        await db.SaveChangesAsync();

        rebuilt.ZipKey.ShouldBeNull();
    }

    [DockerFact]
    public async Task An_item_needing_review_is_packaged_separately_with_its_findings()
    {
        var weekStart = new DateOnly(2036, 1, 7).AddDays(7 * Interlocked.Increment(ref _week));

        var scope = fixture.CreateScope();
        var seeder = scope.ServiceProvider.GetRequiredService<GoldenTenantSeeder>();
        var seeded = await seeder.SeedAsync();
        scope.ServiceProvider.GetRequiredService<IMutableTenantContext>().SetTenant(seeded.TenantId);

        var brandReader = scope.ServiceProvider.GetRequiredService<IBrandBrainReader>();
        var version = await brandReader.CaptureVersionAsync(seeded.BrandId);

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<IObjectStore>();

        var campaign = new ContentCampaign(seeded.TenantId, seeded.BrandId, weekStart, CampaignTrigger.Manual, version.VersionId, 200_000_000);
        db.ContentCampaigns.Add(campaign);

        var item = new ContentItem(
            seeded.TenantId, campaign.Id, ContentItemType.StaticPost,
            "Ran out of remediation attempts", "problem-solution", "n/a", DayOfWeek.Tuesday, 1);
        db.ContentItems.Add(item);
        await db.SaveChangesAsync();

        var bytes = FakePng(600, 750);
        var sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var key = ObjectKey.ForTenant(seeded.TenantId, $"runs/{item.Id:N}/attempts/2/{sha256}.png");
        await using (var content = new MemoryStream(bytes))
        {
            await store.PutAsync(key, content, "image/png", default);
        }

        var asset = new ContentAsset(seeded.TenantId, item.Id, 2, ContentAssetKind.Image, key, "image/png", sha256, bytes.Length, Now, 600, 750);
        db.ContentAssets.Add(asset);
        await db.SaveChangesAsync();

        db.QualityReviews.Add(new Domain.Quality.QualityReview(
            seeded.TenantId, item.Id, 2, Domain.Quality.QaGate.Visual, Domain.Quality.QaOutcome.Fail, 0.2,
            [new Domain.Quality.QaFinding { Code = Domain.Quality.QaFindingCode.OffBrand, Severity = Domain.Quality.QaSeverity.Blocking, Confidence = 0.9, Detail = "Colours drift from the brand palette" }],
            Now));
        await db.SaveChangesAsync();

        item.PromoteBestAttempt(asset.Id);
        item.SendToHumanReview("Quality attempts exhausted.");
        await db.SaveChangesAsync();

        var packager = scope.ServiceProvider.GetRequiredService<CampaignPackager>();
        var package = await packager.BuildAsync(campaign, default);
        await db.SaveChangesAsync();

        var manifest = JsonSerializer.Deserialize<JsonElement>(package.ManifestJson);
        var files = manifest.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("path").GetString()).ToList();

        files.ShouldContain("_needs-review/item-01/image.png");
        files.ShouldContain("_needs-review/item-01/metadata.json");
        files.ShouldNotContain(p => p!.StartsWith("post-", StringComparison.Ordinal));

        var metadataKey = ObjectKey.ForTenant(campaign.TenantId, $"campaigns/{campaign.Id:N}/_needs-review/item-01/metadata.json");
        await using var metadataStream = await store.GetAsync(metadataKey);
        var metadata = await JsonSerializer.DeserializeAsync<JsonElement>(metadataStream);

        var findings = metadata.GetProperty("review").GetProperty("findings");
        findings.GetArrayLength().ShouldBe(1);
        findings[0].GetProperty("code").GetString().ShouldBe("OffBrand");
        metadata.GetProperty("review").GetProperty("failure_reason").GetString().ShouldBe("Quality attempts exhausted.");

        await using var _scope = scope;
    }

    private async Task<(ContentCampaign Campaign, ContentItem Item, AsyncServiceScope Scope)> SetupApprovedItemAsync()
    {
        var weekStart = new DateOnly(2036, 1, 7).AddDays(7 * Interlocked.Increment(ref _week));

        var scope = fixture.CreateScope();
        var seeder = scope.ServiceProvider.GetRequiredService<GoldenTenantSeeder>();
        var seeded = await seeder.SeedAsync();
        scope.ServiceProvider.GetRequiredService<IMutableTenantContext>().SetTenant(seeded.TenantId);

        var brandReader = scope.ServiceProvider.GetRequiredService<IBrandBrainReader>();
        var version = await brandReader.CaptureVersionAsync(seeded.BrandId);

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<IObjectStore>();

        var campaign = new ContentCampaign(seeded.TenantId, seeded.BrandId, weekStart, CampaignTrigger.Manual, version.VersionId, 200_000_000);
        db.ContentCampaigns.Add(campaign);

        var item = new ContentItem(
            seeded.TenantId, campaign.Id, ContentItemType.StaticPost,
            "Your chair sits empty when someone cancels at 9pm", "problem-solution",
            "Show automatic rebooking.", DayOfWeek.Monday, 1);
        db.ContentItems.Add(item);
        await db.SaveChangesAsync();

        var run = new WorkflowRun(seeded.TenantId, campaign.Id, WorkflowScope.Item, item.Id, Now);
        db.WorkflowRuns.Add(run);
        await db.SaveChangesAsync();

        var step = new WorkflowStep(seeded.TenantId, run.Id, nameof(ContentItemStatus.Writing), 1, Now);
        var copySet = new CopySet
        {
            Slots = [new CopySlot { Id = "headline", Text = "Fill the empty slots in your week" }],
            FactCitations = [],
        };
        step.Succeed(Now, resultJson: JsonSerializer.Serialize(copySet));
        db.WorkflowSteps.Add(step);
        await db.SaveChangesAsync();

        var bytes = FakePng(600, 750);
        var sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var key = ObjectKey.ForTenant(seeded.TenantId, $"runs/{item.Id:N}/attempts/1/{sha256}.png");
        await using (var content = new MemoryStream(bytes))
        {
            await store.PutAsync(key, content, "image/png", default);
        }

        db.ContentAssets.Add(new ContentAsset(
            seeded.TenantId, item.Id, 1, ContentAssetKind.Image,
            key, "image/png", sha256, bytes.Length, Now, 600, 750));

        item.Approve();
        await db.SaveChangesAsync();

        return (campaign, item, scope);
    }

    private static byte[] FakePng(int width, int height)
    {
        using var image = new MagickImage(MagickColors.White, (uint)width, (uint)height);
        image.Format = MagickFormat.Png;
        return image.ToByteArray();
    }
}
