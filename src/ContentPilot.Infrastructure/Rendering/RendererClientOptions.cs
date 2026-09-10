namespace ContentPilot.Infrastructure.Rendering;

public sealed class RendererClientOptions
{
    public const string SectionName = "Renderer";

    /// <summary>The renderer sidecar's base address, e.g. <c>http://renderer:8081</c>.</summary>
    public string BaseUrl { get; init; } = "http://localhost:8081";

    /// <summary>
    /// A render involves launching a page, waiting on fonts and screenshotting at 2x —
    /// comfortably under a second normally, but Chromium cold-starting after a deploy can
    /// take longer. Long enough to absorb that without every deploy causing a wave of
    /// transient-retry counters ticking up for no real reason.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(45);
}
