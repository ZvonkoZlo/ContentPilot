using ContentPilot.Rendering.Contracts;
using ContentPilot.Renderer.Engine;
using ContentPilot.Renderer.Imaging;

namespace ContentPilot.Renderer.Endpoints;

/// <summary>
/// The renderer's whole surface. It is a sidecar with an HTTP door, not a microservice:
/// it owns no state, knows nothing about tenants or campaigns, and answers only what it is
/// asked. Everything it needs arrives in the request, because its network is blocked.
/// </summary>
public static class RenderEndpoints
{
    public static IEndpointRouteBuilder MapRenderEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/templates", (TemplateCatalog catalog) => Results.Ok(catalog.Manifests))
            .WithSummary("Lists every template manifest. The orchestrator's template selector reads this.");

        app.MapGet("/templates/{templateId}", (string templateId, TemplateCatalog catalog) =>
            catalog.TryGet(templateId, out var entry)
                ? Results.Ok(entry.Manifest)
                : Results.NotFound(new { detail = $"No template with id '{templateId}'." }))
            .WithSummary("Reads one template manifest.");

        app.MapPost("/render/image", async (
            RenderImageRequest request,
            ImageRenderService renderer,
            CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await renderer.RenderAsync(request, ct));
            }
            catch (TemplateNotFoundException ex)
            {
                return Results.NotFound(new { detail = ex.Message });
            }
            catch (RenderFailedException ex)
            {
                // A spec the template cannot satisfy is the caller's bug, not a server
                // fault, and the orchestrator routes remediation on the message.
                return Results.BadRequest(new { detail = ex.Message });
            }
        })
        .WithSummary("Renders one image and returns it with its measurement report.");

        app.MapPost("/compare", (CompareRequest request, FidelityComparer comparer) =>
        {
            try
            {
                return Results.Ok(comparer.Compare(request));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { detail = ex.Message });
            }
        })
        .WithSummary("Verifies that an immutable asset survived the render unchanged.");

        return app;
    }
}
