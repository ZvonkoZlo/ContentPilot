using ContentPilot.Rendering.Contracts;
using ContentPilot.Renderer.Engine;
using ContentPilot.Renderer.Imaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ContentPilot.RendererTests;

/// <summary>
/// Boots the real renderer services once for the suite. Chromium is launched lazily on the
/// first render and recycled at the end, so the whole suite pays for one browser.
/// </summary>
public sealed class RenderFixture : IAsyncLifetime
{
    private ServiceProvider _services = default!;

    public bool Available { get; private set; }

    public string? SkipReason { get; private set; }

    /// <summary>Where rendered output is written so a human can actually look at it.</summary>
    public string ArtifactDirectory { get; private set; } = string.Empty;

    public Task InitializeAsync()
    {
        var projectRoot = FindRepositoryRoot();
        var rendererOutput = Path.Combine(
            projectRoot, "src", "ContentPilot.Renderer", "bin", Configuration, "net10.0");

        ArtifactDirectory = Path.Combine(projectRoot, "artifacts", "render");
        Directory.CreateDirectory(ArtifactDirectory);

        // Chromium lives inside the repository so nothing is installed outside it.
        var browsers = Path.Combine(projectRoot, ".playwright");

        if (!Directory.Exists(browsers))
        {
            SkipReason =
                "Chromium is not installed. Run: " +
                "PLAYWRIGHT_BROWSERS_PATH=./.playwright ./src/ContentPilot.Renderer/bin/Debug/net10.0/playwright.ps1 install chromium";
            return Task.CompletedTask;
        }

        Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", browsers);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Renderer:FontDirectory"] = Path.Combine(rendererOutput, "Fonts"),
                ["Renderer:TemplateDirectory"] = Path.Combine(rendererOutput, "Templates"),
                ["Renderer:MaxConcurrentRenders"] = "2",
                ["Renderer:RenderTimeout"] = "00:00:45",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddSimpleConsole().SetMinimumLevel(LogLevel.Warning));

        services.AddOptions<RendererOptions>()
            .Bind(configuration.GetSection(RendererOptions.SectionName));

        services.AddSingleton(sp => new FontLibrary(
            sp.GetRequiredService<IOptions<RendererOptions>>().Value.FontDirectory,
            sp.GetRequiredService<ILogger<FontLibrary>>()));

        services.AddSingleton<TemplateCatalog>();
        services.AddSingleton<DocumentBuilder>();
        services.AddSingleton<BrowserPool>();
        services.AddSingleton<ContrastAnalyzer>();
        services.AddSingleton<FidelityComparer>();
        services.AddSingleton<ImageRenderService>();

        _services = services.BuildServiceProvider();
        Available = true;

        return Task.CompletedTask;
    }

    public ImageRenderService Renderer => _services.GetRequiredService<ImageRenderService>();

    public TemplateCatalog Catalog => _services.GetRequiredService<TemplateCatalog>();

    public FidelityComparer Comparer => _services.GetRequiredService<FidelityComparer>();

    public void Save(string name, ImagePayload payload)
    {
        var extension = payload.MediaType.Contains("jpeg", StringComparison.OrdinalIgnoreCase) ? "jpg" : "png";
        File.WriteAllBytes(Path.Combine(ArtifactDirectory, $"{name}.{extension}"), payload.ToBytes());
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

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ContentPilot.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root from the test output directory.");
    }
}

[CollectionDefinition(Name)]
public sealed class RenderCollection : ICollectionFixture<RenderFixture>
{
    public const string Name = "renderer";
}

/// <summary>Skips rather than fails when Chromium has not been installed locally.</summary>
public sealed class RenderFactAttribute : FactAttribute
{
    public RenderFactAttribute()
    {
        var browsers = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".playwright");

        if (!Directory.Exists(Path.GetFullPath(browsers)))
        {
            Skip = "Chromium is not installed; see RenderFixture.SkipReason.";
        }
    }
}
