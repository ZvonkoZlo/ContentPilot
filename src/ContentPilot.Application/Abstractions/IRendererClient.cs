using ContentPilot.Rendering.Contracts;

namespace ContentPilot.Application.Abstractions;

/// <summary>
/// The typed door to the Renderer service. It is a plain HTTP client on purpose — the
/// renderer is a sidecar, not a peer, and this interface is exactly its three endpoints
/// (§11's <c>RendererClient/</c>). Nothing about tenants, budgets or workflow state crosses
/// this boundary; those live entirely on this side of it.
/// <para>
/// The two failure shapes are kept apart because the orchestrator treats them differently:
/// a rejected spec is the caller's bug and routes remediation immediately, while an
/// unavailable renderer is exactly what the transient-retry counter in §8 exists for.
/// </para>
/// </summary>
public interface IRendererClient
{
    Task<RenderImageResponse> RenderAsync(RenderImageRequest request, CancellationToken ct = default);

    Task<CompareResult> CompareAsync(CompareRequest request, CancellationToken ct = default);

    Task<TemplateManifest> GetManifestAsync(string templateId, CancellationToken ct = default);

    Task<IReadOnlyList<TemplateManifest>> GetManifestsAsync(CancellationToken ct = default);
}

/// <summary>No template with that id exists on the renderer currently deployed.</summary>
public sealed class RendererTemplateNotFoundException(string templateId)
    : Exception($"No template with id '{templateId}' is known to the renderer.")
{
    public string TemplateId { get; } = templateId;
}

/// <summary>
/// The renderer refused the request outright: a spec it cannot satisfy, or a comparison
/// with a malformed slot reference. This is a bug in whatever built the request, never a
/// reason to retry — the orchestrator routes remediation on it immediately rather than
/// spending a transient-retry attempt on a call that will fail identically every time.
/// </summary>
public sealed class RenderSpecRejectedException(string message) : Exception(message);

/// <summary>
/// The renderer could not be reached, timed out, or returned a server error. Exactly what
/// §8's transient-retry counter exists for — Chromium can be slow to warm up, and a sidecar
/// restarting mid-deploy is not a defect in the spec it was asked to render.
/// </summary>
public sealed class RendererUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);
