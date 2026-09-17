using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ContentPilot.Application.Abstractions;
using ContentPilot.Domain.Content;
using ContentPilot.Domain.Observability;
using ContentPilot.Domain.Quality;
using ContentPilot.Domain.Workflow;
using ContentPilot.Infrastructure.Persistence;
using ContentPilot.IntegrationTests.Infrastructure;
using ContentPilot.TestSupport;
using ImageMagick;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace ContentPilot.IntegrationTests;

/// <summary>
/// The real host, over real HTTP, against the real containers.
/// <para>
/// These exist because a whole class of defect lives only at this boundary and is invisible
/// to the service-level tests: parameter binding, status codes, middleware order. The
/// listing endpoint below shipped returning 500 for a plain GET — a non-nullable
/// <c>bool</c> query parameter is <em>required</em> in minimal APIs, so omitting it is an
/// unhandled exception rather than the obvious default. Nothing below the HTTP layer could
/// have caught it.
/// </para>
/// </summary>
[Collection(ContentPilotCollection.Name)]
public sealed class ApiEndpointTests(ContentPilotFixture fixture) : IAsyncLifetime
{
    private WebApplicationFactory<Program>? _factory;
    private HttpClient _client = default!;
    private Guid _tenantId;
    private Guid _brandId;

    public async Task InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(fixture.Settings));
        });

        _client = _factory.CreateClient();

        var tenant = await _client.PostAsJsonAsync("/api/tenants",
            new { name = "Api Tests", slug = $"api-{Guid.NewGuid():N}"[..12] });

        tenant.EnsureSuccessStatusCode();
        _tenantId = (await tenant.Content.ReadFromJsonAsync<TenantDto>())!.Id;

        _client.DefaultRequestHeaders.Add("X-Tenant-Id", _tenantId.ToString());

        var brand = await _client.PostAsJsonAsync("/api/brands",
            new { name = "Appointso", timeZoneId = "Europe/Zagreb", languages = new[] { "en", "hr" } });

        brand.EnsureSuccessStatusCode();
        _brandId = (await brand.Content.ReadFromJsonAsync<BrandDto>())!.Id;
    }

    public Task DisposeAsync()
    {
        _client?.Dispose();
        _factory?.Dispose();

        return Task.CompletedTask;
    }

    [DockerFact]
    public async Task Listing_assets_without_any_query_parameters_succeeds()
    {
        var response = await _client.GetAsync($"/api/brands/{_brandId}/assets");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await response.Content.ReadFromJsonAsync<List<AssetDto>>()).ShouldBeEmpty();
    }

    [DockerFact]
    public async Task Listing_assets_honours_its_optional_filters()
    {
        (await _client.GetAsync($"/api/brands/{_brandId}/assets?kind=Logo")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _client.GetAsync($"/api/brands/{_brandId}/assets?includeArchived=true")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [DockerFact]
    public async Task A_brand_with_no_profile_reports_that_rather_than_failing()
    {
        var response = await _client.GetAsync($"/api/brands/{_brandId}/profile");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [DockerFact]
    public async Task A_notification_email_can_be_set_and_cleared_per_brand()
    {
        var set = await _client.PutAsJsonAsync($"/api/brands/{_brandId}/notification-email",
            new { notificationEmail = "owner@example.com" });

        set.StatusCode.ShouldBe(HttpStatusCode.OK, await set.Content.ReadAsStringAsync());
        (await set.Content.ReadFromJsonAsync<BrandDto>())!.NotificationEmail.ShouldBe("owner@example.com");

        var clear = await _client.PutAsJsonAsync($"/api/brands/{_brandId}/notification-email",
            new { notificationEmail = (string?)null });

        clear.StatusCode.ShouldBe(HttpStatusCode.OK, await clear.Content.ReadAsStringAsync());
        (await clear.Content.ReadFromJsonAsync<BrandDto>())!.NotificationEmail.ShouldBeNull();
    }

    [DockerFact]
    public async Task An_invalid_notification_email_is_rejected()
    {
        var response = await _client.PutAsJsonAsync($"/api/brands/{_brandId}/notification-email",
            new { notificationEmail = "not an email" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [DockerFact]
    public async Task A_profile_round_trips_through_the_api()
    {
        var put = await _client.PutAsJsonAsync($"/api/brands/{_brandId}/profile", new
        {
            visual = new { primaryColor = "#6C4CF1", accentColor = "#F5A8C8", styleKeywords = new[] { "clean" } },
            voice = new { summary = "Plain and practical.", bannedWords = new[] { "revolutionary" } },
            messaging = new { positioning = "Online booking for small salons." },
        });

        put.StatusCode.ShouldBe(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());

        var get = await _client.GetFromJsonAsync<ProfileDto>($"/api/brands/{_brandId}/profile");

        get!.Visual.PrimaryColor.ShouldBe("#6C4CF1");
        get.Voice.BannedWords.ShouldContain("revolutionary");
    }

    [DockerFact]
    public async Task A_colour_that_would_reach_a_stylesheet_is_rejected()
    {
        var response = await _client.PutAsJsonAsync($"/api/brands/{_brandId}/profile", new
        {
            visual = new { primaryColor = "red; } body { display:none" },
            voice = new { summary = "Plain." },
            messaging = new { positioning = "Booking." },
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("hex colour");
    }

    [DockerFact]
    public async Task A_duplicate_fact_key_is_a_conflict_not_a_crash()
    {
        var body = new { key = "whatsapp-reminders", statement = "Reminders go over WhatsApp.", category = "Integration" };

        (await _client.PostAsJsonAsync($"/api/brands/{_brandId}/facts", body)).StatusCode.ShouldBe(HttpStatusCode.Created);

        // The key is what copy cites; two facts sharing one would make a citation ambiguous.
        (await _client.PostAsJsonAsync($"/api/brands/{_brandId}/facts", body)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [DockerFact]
    public async Task An_invalid_fact_key_is_refused_with_a_reason()
    {
        var response = await _client.PostAsJsonAsync($"/api/brands/{_brandId}/facts",
            new { key = "Not A Valid Key!", statement = "Something.", category = "Feature" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [DockerFact]
    public async Task A_quota_of_nothing_is_refused()
    {
        var response = await _client.PutAsJsonAsync($"/api/brands/{_brandId}/preferences",
            new { postsPerWeek = 0, carouselsPerWeek = 0, reelsPerWeek = 0 });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [DockerFact]
    public async Task The_brand_block_endpoint_returns_a_budgeted_block()
    {
        var block = await _client.GetFromJsonAsync<BrandBlockDto>($"/api/brands/{_brandId}/brand-block");

        block!.Block.ShouldContain("## Brand: Appointso");
        block.EstimatedTokens.ShouldBeLessThan(block.TokenBudget);
    }

    [DockerFact]
    public async Task An_upload_that_is_not_an_image_is_a_bad_request_not_a_server_error()
    {
        using var content = new MultipartFormDataContent();
        var bytes = new byte[2048];
        bytes[0] = 0x4D;
        bytes[1] = 0x5A;

        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");

        content.Add(new StringContent("Logo"), "kind");
        content.Add(file, "file", "logo.png");

        var response = await _client.PostAsync($"/api/brands/{_brandId}/assets", content);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("signature");
    }

    [DockerFact]
    public async Task Another_tenant_cannot_see_this_brand()
    {
        var other = await _client.PostAsJsonAsync("/api/tenants",
            new { name = "Other", slug = $"other-{Guid.NewGuid():N}"[..12] });

        var otherId = (await other.Content.ReadFromJsonAsync<TenantDto>())!.Id;

        using var stranger = _factory!.CreateClient();
        stranger.DefaultRequestHeaders.Add("X-Tenant-Id", otherId.ToString());

        // 404 rather than 403: a probing caller must not learn the identifier exists.
        (await stranger.GetAsync($"/api/brands/{_brandId}/assets")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await stranger.GetAsync($"/api/brands/{_brandId}/profile")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await stranger.GetAsync($"/api/brands/{_brandId}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [DockerFact]
    public async Task Triggering_a_campaign_enqueues_it_and_returns_its_id()
    {
        var response = await _client.PostAsJsonAsync("/api/campaigns", new { brandId = _brandId });

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        var campaign = await response.Content.ReadFromJsonAsync<CampaignDto>();

        campaign!.Status.ShouldBe("Draft");
        campaign.BrandId.ShouldBe(_brandId);

        var read = await _client.GetAsync($"/api/campaigns/{campaign.Id}");
        read.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [DockerFact]
    public async Task Listing_campaigns_filters_by_brand_and_finds_a_just_triggered_one()
    {
        var trigger = await _client.PostAsJsonAsync("/api/campaigns", new { brandId = _brandId, weekStart = new DateOnly(2032, 7, 5) });
        var campaign = await trigger.Content.ReadFromJsonAsync<CampaignDto>();

        var response = await _client.GetAsync($"/api/campaigns?brandId={_brandId}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var campaigns = (await response.Content.ReadFromJsonAsync<List<CampaignDto>>())!;
        campaigns.ShouldContain(c => c.Id == campaign!.Id);
        campaigns.ShouldAllBe(c => c.BrandId == _brandId);
    }

    [DockerFact]
    public async Task A_second_trigger_for_the_same_week_is_a_conflict_not_a_duplicate()
    {
        var weekStart = new DateOnly(2032, 3, 1);

        var first = await _client.PostAsJsonAsync("/api/campaigns", new { brandId = _brandId, weekStart });
        first.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var second = await _client.PostAsJsonAsync("/api/campaigns", new { brandId = _brandId, weekStart });

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [DockerFact]
    public async Task Triggering_a_campaign_for_an_unknown_brand_is_not_found()
    {
        var response = await _client.PostAsJsonAsync("/api/campaigns", new { brandId = Guid.NewGuid() });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [DockerFact]
    public async Task A_freshly_triggered_campaign_has_no_items_yet()
    {
        var trigger = await _client.PostAsJsonAsync("/api/campaigns", new { brandId = _brandId, weekStart = new DateOnly(2032, 3, 8) });
        var campaign = await trigger.Content.ReadFromJsonAsync<CampaignDto>();

        var items = await _client.GetAsync($"/api/campaigns/{campaign!.Id}/items");

        items.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await items.Content.ReadFromJsonAsync<List<ItemDto>>()).ShouldBeEmpty();
    }

    [DockerFact]
    public async Task A_freshly_triggered_campaign_has_a_null_qa_pass_rate_since_nothing_is_terminal()
    {
        var trigger = await _client.PostAsJsonAsync("/api/campaigns", new { brandId = _brandId, weekStart = new DateOnly(2032, 5, 10) });
        var campaign = await trigger.Content.ReadFromJsonAsync<CampaignDto>();

        var response = await _client.GetAsync($"/api/campaigns/{campaign!.Id}/qa-pass-rate");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var report = await response.Content.ReadFromJsonAsync<QaPassRateDto>();
        report!.FirstAttemptPassRate.ShouldBeNull();
        report.TotalItems.ShouldBe(0);
    }

    [DockerFact]
    public async Task An_unknown_campaign_has_no_qa_pass_rate_to_read()
    {
        var response = await _client.GetAsync($"/api/campaigns/{Guid.NewGuid()}/qa-pass-rate");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [DockerFact]
    public async Task Cancelling_a_draft_campaign_takes_it_out_of_play()
    {
        var trigger = await _client.PostAsJsonAsync("/api/campaigns", new { brandId = _brandId, weekStart = new DateOnly(2032, 3, 15) });
        var campaign = await trigger.Content.ReadFromJsonAsync<CampaignDto>();

        var cancel = await _client.PostAsync($"/api/campaigns/{campaign!.Id}/cancel", null);

        cancel.StatusCode.ShouldBe(HttpStatusCode.OK);
        var cancelled = await cancel.Content.ReadFromJsonAsync<CampaignDto>();
        cancelled!.Status.ShouldBe("Cancelled");
    }

    [DockerFact]
    public async Task Cancelling_an_already_finished_campaign_is_a_harmless_no_op()
    {
        var trigger = await _client.PostAsJsonAsync("/api/campaigns", new { brandId = _brandId, weekStart = new DateOnly(2032, 3, 22) });
        var campaign = await trigger.Content.ReadFromJsonAsync<CampaignDto>();

        var first = await _client.PostAsync($"/api/campaigns/{campaign!.Id}/cancel", null);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);

        var second = await _client.PostAsync($"/api/campaigns/{campaign.Id}/cancel", null);

        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await second.Content.ReadFromJsonAsync<CampaignDto>())!.Status.ShouldBe("Cancelled");
    }

    [DockerFact]
    public async Task Campaign_cost_sums_ledger_entries_and_breaks_them_down_by_agent()
    {
        var trigger = await _client.PostAsJsonAsync("/api/campaigns", new { brandId = _brandId, weekStart = new DateOnly(2032, 5, 17) });
        var campaign = await trigger.Content.ReadFromJsonAsync<CampaignDto>();

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        scope.ServiceProvider.GetRequiredService<IMutableTenantContext>().SetTenant(_tenantId);

        var run = new AgentRun(_tenantId, campaign!.Id, null, "strategist", "v1", Guid.CreateVersion7(), "claude-sonnet-5", 1, DateTimeOffset.UtcNow);
        run.Succeed(TokenUsageSnapshot.Empty, costMicroCents: 0, durationMs: 10, completedAt: DateTimeOffset.UtcNow);
        db.AgentRuns.Add(run);
        await db.SaveChangesAsync();

        db.CostEntries.AddRange(
            new CostEntry(_tenantId, campaign.Id, null, run.Id, CostKind.InputTokens, "anthropic", "claude-sonnet-5", 1000, 300, 30_000, DateTimeOffset.UtcNow),
            new CostEntry(_tenantId, campaign.Id, null, run.Id, CostKind.OutputTokens, "anthropic", "claude-sonnet-5", 500, 1500, 75_000, DateTimeOffset.UtcNow),
            new CostEntry(_tenantId, campaign.Id, null, null, CostKind.RenderSeconds, "renderer", "chromium", 2, 0, 0, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();

        var response = await _client.GetAsync($"/api/campaigns/{campaign.Id}/cost");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var cost = await response.Content.ReadFromJsonAsync<CampaignCostDto>();
        cost!.TotalMicroCents.ShouldBe(105_000);
        cost.ByAgent.ShouldHaveSingleItem();
        cost.ByAgent[0].AgentName.ShouldBe("strategist");
        cost.ByAgent[0].MicroCents.ShouldBe(105_000);
        cost.ByAgent[0].Calls.ShouldBe(2);
        cost.RemainingMicroCents.ShouldBe(cost.BudgetMicroCents - 105_000);
    }

    [DockerFact]
    public async Task A_campaign_that_has_not_been_packaged_yet_has_no_package()
    {
        var trigger = await _client.PostAsJsonAsync("/api/campaigns", new { brandId = _brandId, weekStart = new DateOnly(2032, 3, 29) });
        var campaign = await trigger.Content.ReadFromJsonAsync<CampaignDto>();

        var package = await _client.GetAsync($"/api/campaigns/{campaign!.Id}/package");

        package.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [DockerFact]
    public async Task A_campaign_that_has_not_been_packaged_yet_cannot_be_downloaded()
    {
        var trigger = await _client.PostAsJsonAsync("/api/campaigns", new { brandId = _brandId, weekStart = new DateOnly(2032, 4, 26) });
        var campaign = await trigger.Content.ReadFromJsonAsync<CampaignDto>();

        var download = await _client.PostAsync($"/api/campaigns/{campaign!.Id}/download", null);

        download.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [DockerFact]
    public async Task Downloading_a_packaged_campaign_returns_a_url_and_reuses_it_on_a_second_call()
    {
        var trigger = await _client.PostAsJsonAsync("/api/campaigns", new { brandId = _brandId, weekStart = new DateOnly(2032, 5, 3) });
        var campaign = await trigger.Content.ReadFromJsonAsync<CampaignDto>();

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var packager = scope.ServiceProvider.GetRequiredService<ContentPilot.Infrastructure.Packaging.CampaignPackager>();
        scope.ServiceProvider.GetRequiredService<IMutableTenantContext>().SetTenant(_tenantId);

        var loaded = await db.ContentCampaigns.SingleAsync(c => c.Id == campaign!.Id);
        await packager.BuildAsync(loaded, default);
        await db.SaveChangesAsync();

        var first = await _client.PostAsync($"/api/campaigns/{campaign!.Id}/download", null);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var firstUrl = (await first.Content.ReadFromJsonAsync<DownloadDto>())!.Url;
        firstUrl.ShouldNotBeNullOrWhiteSpace();

        var zipKeyAfterFirst = (await db.CampaignPackages.AsNoTracking().SingleAsync(p => p.CampaignId == campaign.Id)).ZipKey;
        zipKeyAfterFirst.ShouldNotBeNullOrWhiteSpace();

        var second = await _client.PostAsync($"/api/campaigns/{campaign.Id}/download", null);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);

        var zipKeyAfterSecond = (await db.CampaignPackages.AsNoTracking().SingleAsync(p => p.CampaignId == campaign.Id)).ZipKey;
        zipKeyAfterSecond.ShouldBe(zipKeyAfterFirst);
    }

    [DockerFact]
    public async Task Rating_an_item_round_trips_and_a_second_rating_replaces_the_first()
    {
        var trigger = await _client.PostAsJsonAsync("/api/campaigns", new { brandId = _brandId, weekStart = new DateOnly(2032, 4, 5) });
        var campaign = await trigger.Content.ReadFromJsonAsync<CampaignDto>();

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        scope.ServiceProvider.GetRequiredService<IMutableTenantContext>().SetTenant(_tenantId);

        var item = new ContentItem(
            _tenantId, campaign!.Id, ContentItemType.StaticPost, "Rate me", "problem-solution", "n/a", DayOfWeek.Monday, 1);
        db.ContentItems.Add(item);
        await db.SaveChangesAsync();

        var first = await _client.PostAsJsonAsync(
            $"/api/campaigns/{campaign.Id}/items/{item.Id}/rating", new { score = 4, note = "Good enough to ship" });

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var firstRating = await first.Content.ReadFromJsonAsync<RatingDto>();
        firstRating!.Score.ShouldBe(4);

        var second = await _client.PostAsJsonAsync(
            $"/api/campaigns/{campaign.Id}/items/{item.Id}/rating", new { score = 5, note = (string?)null });

        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        var secondRating = await second.Content.ReadFromJsonAsync<RatingDto>();
        secondRating!.ContentItemId.ShouldBe(firstRating.ContentItemId);
        secondRating.Score.ShouldBe(5);

        (await db.HumanRatings.CountAsync(r => r.ContentItemId == item.Id)).ShouldBe(1);
    }

    [DockerFact]
    public async Task Rating_an_item_from_the_wrong_campaign_is_not_found()
    {
        var first = await _client.PostAsJsonAsync("/api/campaigns", new { brandId = _brandId, weekStart = new DateOnly(2032, 4, 12) });
        var second = await _client.PostAsJsonAsync("/api/campaigns", new { brandId = _brandId, weekStart = new DateOnly(2032, 4, 19) });
        var campaignA = await first.Content.ReadFromJsonAsync<CampaignDto>();
        var campaignB = await second.Content.ReadFromJsonAsync<CampaignDto>();

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        scope.ServiceProvider.GetRequiredService<IMutableTenantContext>().SetTenant(_tenantId);

        var item = new ContentItem(
            _tenantId, campaignA!.Id, ContentItemType.StaticPost, "Belongs to A", "problem-solution", "n/a", DayOfWeek.Monday, 1);
        db.ContentItems.Add(item);
        await db.SaveChangesAsync();

        var response = await _client.PostAsJsonAsync(
            $"/api/campaigns/{campaignB!.Id}/items/{item.Id}/rating", new { score = 3, note = (string?)null });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [DockerFact]
    public async Task Approving_a_needs_review_item_promotes_it_and_leaves_a_history_entry()
    {
        var trigger = await _client.PostAsJsonAsync("/api/campaigns", new { brandId = _brandId, weekStart = new DateOnly(2032, 5, 24) });
        var campaign = await trigger.Content.ReadFromJsonAsync<CampaignDto>();

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<IObjectStore>();
        scope.ServiceProvider.GetRequiredService<IMutableTenantContext>().SetTenant(_tenantId);

        var item = new ContentItem(
            _tenantId, campaign!.Id, ContentItemType.StaticPost, "Approved late by a human", "problem-solution", "n/a", DayOfWeek.Monday, 1);
        db.ContentItems.Add(item);
        await db.SaveChangesAsync();

        var run = new WorkflowRun(_tenantId, campaign.Id, WorkflowScope.Item, item.Id, DateTimeOffset.UtcNow);
        db.WorkflowRuns.Add(run);
        await db.SaveChangesAsync();

        var writingStep = new WorkflowStep(_tenantId, run.Id, nameof(ContentItemStatus.Writing), 1, DateTimeOffset.UtcNow);
        writingStep.Succeed(DateTimeOffset.UtcNow, resultJson: """{"slots":[{"id":"headline","text":"Late but worth it"}],"fact_citations":[]}""");
        db.WorkflowSteps.Add(writingStep);
        await db.SaveChangesAsync();

        using var fakeImage = new MagickImage(MagickColors.White, 600u, 750u);
        fakeImage.Format = MagickFormat.Png;
        var bytes = fakeImage.ToByteArray();
        var sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var key = ObjectKey.ForTenant(_tenantId, $"runs/{item.Id:N}/attempts/1/{sha256}.png");
        await using (var content = new MemoryStream(bytes))
        {
            await store.PutAsync(key, content, "image/png", default);
        }

        var asset = new ContentAsset(_tenantId, item.Id, 1, ContentAssetKind.Image, key, "image/png", sha256, bytes.Length, DateTimeOffset.UtcNow, 600, 750);
        db.ContentAssets.Add(asset);
        await db.SaveChangesAsync();

        item.PromoteBestAttempt(asset.Id);
        item.SendToHumanReview("Ran out of attempts.");
        await db.SaveChangesAsync();

        var response = await _client.PostAsync($"/api/campaigns/{campaign.Id}/items/{item.Id}/approve", null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var approved = await response.Content.ReadFromJsonAsync<ItemStatusDto>();
        approved!.Status.ShouldBe("Approved");

        var history = await db.ContentHistory.AsNoTracking().SingleAsync(h => h.ContentItemId == item.Id);
        history.Hook.ShouldBe("Late but worth it");

        var package = await db.CampaignPackages.AsNoTracking().SingleAsync(p => p.CampaignId == campaign.Id);
        package.ManifestJson.ShouldContain("post-01/image.png");
    }

    [DockerFact]
    public async Task Approving_an_item_that_is_not_needs_review_is_a_conflict()
    {
        var trigger = await _client.PostAsJsonAsync("/api/campaigns", new { brandId = _brandId, weekStart = new DateOnly(2032, 5, 31) });
        var campaign = await trigger.Content.ReadFromJsonAsync<CampaignDto>();

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        scope.ServiceProvider.GetRequiredService<IMutableTenantContext>().SetTenant(_tenantId);

        var item = new ContentItem(
            _tenantId, campaign!.Id, ContentItemType.StaticPost, "Still pending", "problem-solution", "n/a", DayOfWeek.Monday, 1);
        db.ContentItems.Add(item);
        await db.SaveChangesAsync();

        var response = await _client.PostAsync($"/api/campaigns/{campaign.Id}/items/{item.Id}/approve", null);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [DockerFact]
    public async Task Rejecting_a_needs_review_item_moves_it_to_failed_with_the_reviewers_reason()
    {
        var trigger = await _client.PostAsJsonAsync("/api/campaigns", new { brandId = _brandId, weekStart = new DateOnly(2032, 6, 7) });
        var campaign = await trigger.Content.ReadFromJsonAsync<CampaignDto>();

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        scope.ServiceProvider.GetRequiredService<IMutableTenantContext>().SetTenant(_tenantId);

        var item = new ContentItem(
            _tenantId, campaign!.Id, ContentItemType.StaticPost, "Not good enough", "problem-solution", "n/a", DayOfWeek.Monday, 1);
        item.SendToHumanReview("Ran out of attempts.");
        db.ContentItems.Add(item);
        await db.SaveChangesAsync();

        var response = await _client.PostAsJsonAsync(
            $"/api/campaigns/{campaign.Id}/items/{item.Id}/reject", new { reason = "Off-brand tone throughout." });

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var rejected = await response.Content.ReadFromJsonAsync<ItemStatusDto>();
        rejected!.Status.ShouldBe("Failed");
        rejected.FailureReason.ShouldBe("Off-brand tone throughout.");

        (await db.ContentHistory.CountAsync(h => h.ContentItemId == item.Id)).ShouldBe(0);
    }

    [DockerFact]
    public async Task Item_detail_carries_its_findings_step_history_and_agent_runs()
    {
        var trigger = await _client.PostAsJsonAsync("/api/campaigns", new { brandId = _brandId, weekStart = new DateOnly(2032, 6, 14) });
        var campaign = await trigger.Content.ReadFromJsonAsync<CampaignDto>();

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        scope.ServiceProvider.GetRequiredService<IMutableTenantContext>().SetTenant(_tenantId);

        var item = new ContentItem(
            _tenantId, campaign!.Id, ContentItemType.StaticPost, "Explainable end to end", "problem-solution", "Prove the run tree works.", DayOfWeek.Monday, 1);
        db.ContentItems.Add(item);
        await db.SaveChangesAsync();

        var run = new WorkflowRun(_tenantId, campaign.Id, WorkflowScope.Item, item.Id, DateTimeOffset.UtcNow);
        db.WorkflowRuns.Add(run);
        await db.SaveChangesAsync();

        var writingStep = new WorkflowStep(_tenantId, run.Id, nameof(ContentItemStatus.Writing), 1, DateTimeOffset.UtcNow);
        writingStep.Succeed(DateTimeOffset.UtcNow);
        db.WorkflowSteps.Add(writingStep);

        db.QualityReviews.Add(new QualityReview(
            _tenantId, item.Id, 1, QaGate.Visual, QaOutcome.NeedsReview, 0.66,
            [new QaFinding { Code = QaFindingCode.PoorComposition, Severity = QaSeverity.Major, Detail = "Subject too far left.", Confidence = 0.8 }],
            DateTimeOffset.UtcNow));

        var agentRun = new AgentRun(_tenantId, campaign.Id, item.Id, "visual-qa", "v1", Guid.CreateVersion7(), "claude-sonnet-5", 1, DateTimeOffset.UtcNow);
        agentRun.Succeed(TokenUsageSnapshot.Empty, costMicroCents: 12_345, durationMs: 400, completedAt: DateTimeOffset.UtcNow);
        db.AgentRuns.Add(agentRun);

        await db.SaveChangesAsync();

        var response = await _client.GetAsync($"/api/campaigns/{campaign.Id}/items/{item.Id}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var detail = await response.Content.ReadFromJsonAsync<ItemDetailDto>();

        detail!.Topic.ShouldBe("Explainable end to end");
        detail.Reviews.ShouldHaveSingleItem();
        detail.Reviews[0].Gate.ShouldBe("Visual");
        detail.Reviews[0].Findings.ShouldHaveSingleItem();
        detail.Reviews[0].Findings[0].Code.ShouldBe("PoorComposition");
        detail.Steps.ShouldContain(s => s.StepName == "Writing" && s.Outcome == "Succeeded");
        detail.AgentRuns.ShouldContain(r => r.AgentName == "visual-qa" && r.CostMicroCents == 12_345);
    }

    [DockerFact]
    public async Task Item_image_returns_the_promoted_asset_without_crossing_campaigns()
    {
        var firstTrigger = await _client.PostAsJsonAsync(
            "/api/campaigns", new { brandId = _brandId, weekStart = new DateOnly(2032, 6, 28) });
        var firstCampaign = await firstTrigger.Content.ReadFromJsonAsync<CampaignDto>();
        var secondTrigger = await _client.PostAsJsonAsync(
            "/api/campaigns", new { brandId = _brandId, weekStart = new DateOnly(2032, 7, 5) });
        var secondCampaign = await secondTrigger.Content.ReadFromJsonAsync<CampaignDto>();

        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<IObjectStore>();
        scope.ServiceProvider.GetRequiredService<IMutableTenantContext>().SetTenant(_tenantId);

        var item = new ContentItem(
            _tenantId, firstCampaign!.Id, ContentItemType.StaticPost, "Preview this render",
            "feature-highlight", "Show the image.", DayOfWeek.Monday, 1);
        db.ContentItems.Add(item);
        await db.SaveChangesAsync();

        var key = ObjectKey.ForTenant(_tenantId, $"runs/{item.Id:N}/attempts/1/preview.png");
        await using (var content = new MemoryStream([1, 2, 3]))
        {
            await store.PutAsync(key, content, "image/png", default);
        }

        var asset = new ContentAsset(
            _tenantId, item.Id, 1, ContentAssetKind.Image, key, "image/png", "preview-hash", 3,
            DateTimeOffset.UtcNow, 600, 750);
        db.ContentAssets.Add(asset);
        await db.SaveChangesAsync();
        item.PromoteBestAttempt(asset.Id);
        await db.SaveChangesAsync();

        var response = await _client.GetAsync($"/api/campaigns/{firstCampaign.Id}/items/{item.Id}/image");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var image = await response.Content.ReadFromJsonAsync<ItemImageDto>();
        image!.Url.ShouldNotBeNullOrWhiteSpace();
        image.MediaType.ShouldBe("image/png");
        image.Width.ShouldBe(600);
        image.Height.ShouldBe(750);

        var wrongCampaign = await _client.GetAsync(
            $"/api/campaigns/{secondCampaign!.Id}/items/{item.Id}/image");
        wrongCampaign.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [DockerFact]
    public async Task An_unknown_item_has_no_detail_to_read()
    {
        var trigger = await _client.PostAsJsonAsync("/api/campaigns", new { brandId = _brandId, weekStart = new DateOnly(2032, 6, 21) });
        var campaign = await trigger.Content.ReadFromJsonAsync<CampaignDto>();

        var response = await _client.GetAsync($"/api/campaigns/{campaign!.Id}/items/{Guid.NewGuid()}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private sealed record CampaignDto(Guid Id, Guid BrandId, string Status);

    private sealed record ItemDto(Guid Id, string Topic);

    private sealed record ItemImageDto(string Url, string MediaType, int Width, int Height);

    private sealed record RatingDto(Guid ContentItemId, int Score, string? Note, DateTimeOffset RatedAt);

    private sealed record ItemStatusDto(
        Guid Id, int Ordinal, string Type, string Topic, string Pillar, string PublishDay, string Status, int QualityAttempts, string? FailureReason);

    private sealed record ItemDetailDto(
        Guid Id, int Ordinal, string Type, string Topic, string Pillar, string Objective, string PublishDay, string Status,
        int QualityAttempts, string? FailureReason,
        List<ItemFindingsDto> Reviews, List<ItemStepDto> Steps, List<ItemAgentRunDto> AgentRuns);

    private sealed record ItemFindingsDto(int Attempt, string Gate, string Outcome, double Score, DateTimeOffset EvaluatedAt, List<ItemFindingDto> Findings);

    private sealed record ItemFindingDto(string Code, string Severity, string? SlotId, string Detail, double? Measured, double? Threshold, double? Confidence);

    private sealed record ItemStepDto(string StepName, int Attempt, string Outcome, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, string? Error);

    private sealed record ItemAgentRunDto(string AgentName, int Attempt, string ModelId, string Outcome, long CostMicroCents, int DurationMs, DateTimeOffset StartedAt);

    private sealed record DownloadDto(string Url);

    private sealed record QaPassRateDto(int TotalItems, int TerminalItems, int Approved, int FirstAttemptPasses, int NeedsReview, int Failed, double? FirstAttemptPassRate);

    private sealed record CampaignCostDto(Guid CampaignId, long TotalMicroCents, long BudgetMicroCents, long RemainingMicroCents, List<CampaignCostByAgentDto> ByAgent);

    private sealed record CampaignCostByAgentDto(string AgentName, long MicroCents, int Calls);

    private sealed record TenantDto(Guid Id);

    private sealed record BrandDto(Guid Id, string? NotificationEmail);

    private sealed record ProfileDto(VisualDto Visual, VoiceDto Voice);

    private sealed record VisualDto(string PrimaryColor);

    private sealed record VoiceDto(string Summary, IReadOnlyList<string> BannedWords);

    private sealed record AssetDto(Guid Id, string Kind);

    private sealed record BrandBlockDto(int EstimatedTokens, int TokenBudget, string Block);
}
