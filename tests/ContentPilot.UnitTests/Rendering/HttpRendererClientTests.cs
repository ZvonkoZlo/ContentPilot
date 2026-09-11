using System.Net;
using System.Net.Http.Json;
using ContentPilot.Application.Abstractions;
using ContentPilot.Infrastructure.Rendering;
using ContentPilot.Rendering.Contracts;
using Shouldly;

namespace ContentPilot.UnitTests.Rendering;

/// <summary>
/// Every HTTP outcome mapped to the domain-shaped exception the orchestrator actually
/// branches on — no real renderer process involved, only the wire contract.
/// </summary>
public sealed class HttpRendererClientTests
{
    private static HttpRendererClient Client(FakeHandler handler) => new(new HttpClient(handler)
    {
        BaseAddress = new Uri("http://renderer.test"),
    });

    private static RenderImageRequest SampleRequest() => new()
    {
        TemplateId = "phone-floating",
        AspectRatio = AspectRatio.FourFive,
        Brand = new BrandTokens { Name = "Appointso", PrimaryColor = "#6C4CF1" },
        Text = new Dictionary<string, string> { ["headline"] = "Fill the empty slots in your week" },
    };

    [Fact]
    public async Task A_successful_render_deserialises_the_response()
    {
        var response = new RenderImageResponse
        {
            TemplateId = "phone-floating",
            TemplateVersion = 3,
            Image = new ImagePayload { MediaType = "image/png", Base64 = "AA==" },
            Report = new RenderReport
            {
                Width = 1080,
                Height = 1350,
                DeviceScaleFactor = 2,
                Slots = [],
                FontsLoaded = ["Archivo"],
                RenderDurationMs = 900,
            },
        };

        var handler = FakeHandler.Returning(HttpStatusCode.OK, response);
        var result = await Client(handler).RenderAsync(SampleRequest());

        result.TemplateId.ShouldBe("phone-floating");
        handler.LastRequest!.RequestUri!.AbsolutePath.ShouldBe("/render/image");
        handler.LastRequest.Method.ShouldBe(HttpMethod.Post);
    }

    [Fact]
    public async Task A_rejected_spec_is_a_permanent_failure_not_a_transient_one()
    {
        var handler = FakeHandler.Returning(HttpStatusCode.BadRequest, new { detail = "Slot 'headline' exceeds its character budget." });

        var ex = await Should.ThrowAsync<RenderSpecRejectedException>(() => Client(handler).RenderAsync(SampleRequest()));
        ex.Message.ShouldContain("character budget");
    }

    [Fact]
    public async Task An_unknown_template_on_render_is_reported_by_id()
    {
        var handler = FakeHandler.Returning(HttpStatusCode.NotFound, new { detail = "not found" });

        var ex = await Should.ThrowAsync<RendererTemplateNotFoundException>(() => Client(handler).RenderAsync(SampleRequest()));
        ex.TemplateId.ShouldBe("phone-floating");
    }

    [Fact]
    public async Task A_server_error_is_transient()
    {
        var handler = FakeHandler.Returning(HttpStatusCode.ServiceUnavailable, new { detail = "starting up" });

        await Should.ThrowAsync<RendererUnavailableException>(() => Client(handler).RenderAsync(SampleRequest()));
    }

    [Fact]
    public async Task A_dropped_connection_is_transient()
    {
        var handler = FakeHandler.Throwing(new HttpRequestException("connection refused"));

        await Should.ThrowAsync<RendererUnavailableException>(() => Client(handler).RenderAsync(SampleRequest()));
    }

    [Fact]
    public async Task A_caller_initiated_cancellation_is_not_reported_as_the_renderer_being_unavailable()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var handler = FakeHandler.Throwing(new TaskCanceledException());

        // The caller cancelled; the renderer said nothing about its own availability. That
        // distinction matters because a cancelled item should not tick the transient-retry
        // counter as if a provider had actually failed.
        await Should.ThrowAsync<TaskCanceledException>(() => Client(handler).RenderAsync(SampleRequest(), cts.Token));
    }

    [Fact]
    public async Task A_malformed_comparison_is_a_permanent_rejection()
    {
        var handler = FakeHandler.Returning(HttpStatusCode.BadRequest, new { detail = "Slot 'missing' has no mask." });

        var request = new CompareRequest
        {
            SlotId = "missing",
            Reference = new ImagePayload { MediaType = "image/png", Base64 = "AA==" },
            Rendered = new ImagePayload { MediaType = "image/png", Base64 = "AA==" },
        };

        await Should.ThrowAsync<RenderSpecRejectedException>(() => Client(handler).CompareAsync(request));
    }

    [Fact]
    public async Task Fetching_an_unknown_manifest_by_id_is_reported_by_id()
    {
        var handler = FakeHandler.Returning(HttpStatusCode.NotFound, new { detail = "not found" });

        var ex = await Should.ThrowAsync<RendererTemplateNotFoundException>(() => Client(handler).GetManifestAsync("does-not-exist"));
        ex.TemplateId.ShouldBe("does-not-exist");
    }

    [Fact]
    public async Task Listing_manifests_deserialises_the_whole_catalog()
    {
        var manifests = new[]
        {
            new TemplateManifest
            {
                TemplateId = "phone-floating",
                Version = 1,
                Name = "Phone floating",
                ContentTypes = [TemplateContentType.Static],
                AspectRatios = [AspectRatio.FourFive],
                TextSlots = [],
            },
        };

        var handler = FakeHandler.Returning(HttpStatusCode.OK, manifests);
        var result = await Client(handler).GetManifestsAsync();

        result.ShouldHaveSingleItem();
        result[0].TemplateId.ShouldBe("phone-floating");
        handler.LastRequest!.Method.ShouldBe(HttpMethod.Get);
    }

    /// <summary>A minimal stand-in for the renderer's HTTP surface — no real socket involved.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public HttpRequestMessage? LastRequest { get; private set; }

        private FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public static FakeHandler Returning<T>(HttpStatusCode status, T body) =>
            new(_ => new HttpResponseMessage(status) { Content = JsonContent.Create(body) });

        public static FakeHandler Throwing(Exception exception) =>
            new(_ => throw exception);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(_respond(request));
        }
    }
}
