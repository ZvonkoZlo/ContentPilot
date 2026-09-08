namespace ContentPilot.Renderer;

/// <summary>
/// Anchor type for assembly-level reflection (architecture tests, future template
/// discovery). The renderer is a standalone service: it must never reference the domain,
/// the application layer, or infrastructure, and a test asserts exactly that.
/// </summary>
public sealed class RendererAssemblyMarker;
