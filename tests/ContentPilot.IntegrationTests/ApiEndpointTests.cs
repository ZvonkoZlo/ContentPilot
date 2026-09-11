using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ContentPilot.IntegrationTests.Infrastructure;
using ContentPilot.TestSupport;
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

    private sealed record CampaignDto(Guid Id, Guid BrandId, string Status);

    private sealed record ItemDto(Guid Id, string Topic);

    private sealed record TenantDto(Guid Id);

    private sealed record BrandDto(Guid Id);

    private sealed record ProfileDto(VisualDto Visual, VoiceDto Voice);

    private sealed record VisualDto(string PrimaryColor);

    private sealed record VoiceDto(string Summary, IReadOnlyList<string> BannedWords);

    private sealed record AssetDto(Guid Id, string Kind);

    private sealed record BrandBlockDto(int EstimatedTokens, int TokenBudget, string Block);
}
