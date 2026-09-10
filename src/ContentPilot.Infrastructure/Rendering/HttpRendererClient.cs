using System.Net;
using System.Net.Http.Json;
using ContentPilot.Application.Abstractions;
using ContentPilot.Rendering.Contracts;

namespace ContentPilot.Infrastructure.Rendering;

/// <summary>
/// The one place this system talks to the Renderer service. Everything else — the
/// orchestrator's steps, the QA suite that reads the response's <c>RenderReport</c> — deals
/// only in <see cref="ContentPilot.Rendering.Contracts"/> types, never in HTTP.
/// <para>
/// <see cref="HttpClient"/>'s base address and timeout are configured once, on the named
/// client this is constructed against (see the DI registration), so this type owns no
/// connection concerns of its own — only how one call's outcome becomes a domain-shaped
/// exception. The renderer's own default JSON options (camelCase, enums as strings via
/// attributes already on the contract types) are what <c>System.Text.Json</c>'s defaults
/// produce on both sides, so no custom serializer settings travel with this client.
/// </para>
/// </summary>
public sealed class HttpRendererClient(HttpClient http) : IRendererClient
{
    public async Task<RenderImageResponse> RenderAsync(RenderImageRequest request, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Post, "/render/image", request, ct);

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            // The renderer's own contract: a spec it cannot satisfy is a 400, not a 5xx.
            // That is the caller's bug, so it is never worth a transient retry.
            throw new RenderSpecRejectedException(await ReadDetailAsync(response, ct));
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new RendererTemplateNotFoundException(request.TemplateId);
        }

        return await ReadOrThrowAsync<RenderImageResponse>(response, ct);
    }

    public async Task<CompareResult> CompareAsync(CompareRequest request, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Post, "/compare", request, ct);

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            throw new RenderSpecRejectedException(await ReadDetailAsync(response, ct));
        }

        return await ReadOrThrowAsync<CompareResult>(response, ct);
    }

    public async Task<TemplateManifest> GetManifestAsync(string templateId, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/templates/{Uri.EscapeDataString(templateId)}", null, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new RendererTemplateNotFoundException(templateId);
        }

        return await ReadOrThrowAsync<TemplateManifest>(response, ct);
    }

    public async Task<IReadOnlyList<TemplateManifest>> GetManifestsAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "/templates", null, ct);

        return await ReadOrThrowAsync<IReadOnlyList<TemplateManifest>>(response, ct);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path);

            if (body is not null)
            {
                request.Content = JsonContent.Create(body);
            }

            return await http.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // A dropped connection or a timed-out request is indistinguishable from a
            // sidecar that has not finished warming up — both are exactly what the
            // transient-retry counter in §8 exists for, not a reason to fail the item.
            throw new RendererUnavailableException($"The renderer did not respond to {method} {path}.", ex);
        }
    }

    private static async Task<T> ReadOrThrowAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new RendererUnavailableException(
                $"The renderer returned {(int)response.StatusCode} {response.ReasonPhrase} for {response.RequestMessage?.RequestUri}: " +
                await ReadDetailAsync(response, ct));
        }

        return await response.Content.ReadFromJsonAsync<T>(ct)
            ?? throw new RendererUnavailableException("The renderer returned an empty body for a successful response.");
    }

    private static async Task<string> ReadDetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemBody>(ct);

            return problem?.Detail ?? await response.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            return $"(no readable body, status {(int)response.StatusCode})";
        }
    }

    /// <summary>Matches the shape the renderer's endpoints actually return: <c>{ "detail": "..." }</c>.</summary>
    private sealed record ProblemBody(string? Detail);
}
