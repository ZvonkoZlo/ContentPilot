using ContentPilot.Rendering.Contracts;
using ContentPilot.Renderer.Engine;
using ContentPilot.Renderer.Imaging;
using ContentPilot.Renderer.Video;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ContentPilot.RendererTests.Video;

public sealed class ReelLayerFixture : IAsyncLifetime
{
    private ServiceProvider _services = default!;
    private readonly Dictionary<string, Task<IReadOnlyList<RenderedSceneLayers>>> _rendered = new(StringComparer.Ordinal);

    public ReelLayerRenderer Renderer => _services.GetRequiredService<ReelLayerRenderer>();

    public ReelTemplateCatalog Catalog => _services.GetRequiredService<ReelTemplateCatalog>();

    public IReelRenderer FullRenderer => _services.GetRequiredService<IReelRenderer>();

    public Task<IReadOnlyList<RenderedSceneLayers>> RenderLayersAsync(ReelTemplateManifest manifest)
    {
        lock (_rendered)
        {
            if (!_rendered.TryGetValue(manifest.TemplateId, out var rendered))
            {
                rendered = Renderer.RenderAsync(ReelSamples.For(manifest), manifest, CancellationToken.None);
                _rendered.Add(manifest.TemplateId, rendered);
            }

            return rendered;
        }
    }

    public Task InitializeAsync()
    {
        var root = ReelManifestTests.FindRepositoryRoot();
        var rendererOutput = Path.Combine(root, "src", "ContentPilot.Renderer", "bin", Configuration, "net10.0");
        var browsers = Path.Combine(root, ".playwright");

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH")) &&
            Directory.Exists(browsers))
        {
            Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", browsers);
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Renderer:FontDirectory"] = Path.Combine(rendererOutput, "Fonts"),
                ["Renderer:TemplateDirectory"] = Path.Combine(rendererOutput, "Templates"),
                ["Renderer:MaxConcurrentRenders"] = "4",
                ["Renderer:RenderTimeout"] = "00:00:45",
            })
            .Build();
        var services = new ServiceCollection();

        services.AddLogging(builder => builder.AddSimpleConsole().SetMinimumLevel(LogLevel.Warning));
        services.AddOptions<RendererOptions>().Bind(configuration.GetSection(RendererOptions.SectionName));
        services.AddSingleton(provider => new FontLibrary(
            provider.GetRequiredService<IOptions<RendererOptions>>().Value.FontDirectory,
            provider.GetRequiredService<ILogger<FontLibrary>>()));
        services.AddSingleton<BrowserPool>();
        services.AddSingleton<ContrastAnalyzer>();
        services.AddSingleton<ReelDocumentBuilder>();
        services.AddSingleton<ReelTemplateCatalog>();
        services.AddSingleton<ReelLayerRenderer>();
        services.AddSingleton(new ReelRendererOptions
        {
            FfmpegPath = Environment.GetEnvironmentVariable("CONTENTPILOT_FFMPEG_PATH") ?? "ffmpeg",
            FfprobePath = Environment.GetEnvironmentVariable("CONTENTPILOT_FFPROBE_PATH") ?? "ffprobe",
            ProcessTimeout = TimeSpan.FromSeconds(15),
        });
        services.AddSingleton<FfmpegRunner>();
        services.AddSingleton<SceneComposer>();
        services.AddSingleton<ReelKeyframeExtractor>();
        services.AddSingleton<ReelQaService>();
        services.AddSingleton<FfmpegReelRenderer>();
        services.AddSingleton<IReelRenderer>(provider => provider.GetRequiredService<FfmpegReelRenderer>());

        _services = services.BuildServiceProvider();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }
    }

    private static string Configuration =>
#if DEBUG
        "Debug";
#else
        "Release";
#endif
}

[CollectionDefinition(Name)]
public sealed class ReelLayerCollection : ICollectionFixture<ReelLayerFixture>
{
    public const string Name = "reel-layers";
}
