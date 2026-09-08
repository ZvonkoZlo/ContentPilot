using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace ContentPilot.Renderer.Engine;

/// <summary>
/// One long-lived Chromium, a fresh browser context per render, and a hard cap on
/// concurrency. Contexts are cheap and isolated; browsers are expensive and leak memory
/// over thousands of pages, so the browser is recycled on a counter rather than trusted to
/// stay healthy indefinitely.
/// </summary>
public sealed class BrowserPool : IAsyncDisposable
{
    private readonly RendererOptions _options;
    private readonly ILogger<BrowserPool> _logger;
    private readonly SemaphoreSlim _concurrency;
    private readonly SemaphoreSlim _browserLock = new(1, 1);

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private int _rendersOnCurrentBrowser;

    public BrowserPool(IOptions<RendererOptions> options, ILogger<BrowserPool> logger)
    {
        _options = options.Value;
        _logger = logger;
        _concurrency = new SemaphoreSlim(_options.MaxConcurrentRenders, _options.MaxConcurrentRenders);
    }

    /// <summary>
    /// Runs <paramref name="work"/> against an isolated page. The page's network is blocked
    /// before any content is set: the renderer executes markup shaped by tenant data, so it
    /// must never resolve an address anyone else chose.
    /// </summary>
    public async Task<T> UsePageAsync<T>(Func<IPage, List<string>, Task<T>> work, CancellationToken ct)
    {
        await _concurrency.WaitAsync(ct);

        try
        {
            var browser = await GetBrowserAsync(ct);

            await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                DeviceScaleFactor = _options.DeviceScaleFactor,
                ViewportSize = new ViewportSize { Width = 1080, Height = 1350 },
                ReducedMotion = ReducedMotion.Reduce,
                ColorScheme = Microsoft.Playwright.ColorScheme.Light,
                JavaScriptEnabled = true,
                Offline = true,
                BypassCSP = false,
            });

            var page = await context.NewPageAsync();
            page.SetDefaultTimeout((float)_options.RenderTimeout.TotalMilliseconds);

            var blocked = new List<string>();

            // Everything the document needs is inlined. A request leaving the page means a
            // template referenced something external, and that must fail loudly rather than
            // fall back to a default face or a missing image.
            await page.RouteAsync("**/*", route =>
            {
                var url = route.Request.Url;

                if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                    url.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
                {
                    return route.ContinueAsync();
                }

                lock (blocked)
                {
                    blocked.Add(url);
                }

                return route.AbortAsync();
            });

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_options.RenderTimeout);

            return await work(page, blocked);
        }
        finally
        {
            _concurrency.Release();
        }
    }

    private async Task<IBrowser> GetBrowserAsync(CancellationToken ct)
    {
        await _browserLock.WaitAsync(ct);

        try
        {
            var recycle = _browser is { IsConnected: true } &&
                          _rendersOnCurrentBrowser >= _options.RecycleBrowserAfterRenders;

            if (recycle)
            {
                _logger.LogInformation(
                    "Recycling Chromium after {Count} renders to cap memory growth.", _rendersOnCurrentBrowser);

                await _browser!.CloseAsync();
                _browser = null;
            }

            if (_browser is { IsConnected: true })
            {
                _rendersOnCurrentBrowser++;
                return _browser;
            }

            _playwright ??= await Playwright.CreateAsync();

            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                Args =
                [
                    "--disable-lcd-text",          // subpixel AA varies with GPU; goldens must not
                    "--font-render-hinting=none",  // hinting differs across hosts
                    "--disable-gpu",
                    "--hide-scrollbars",
                    "--force-color-profile=srgb",
                    "--disable-dev-shm-usage",
                ],
            });

            _rendersOnCurrentBrowser = 1;

            _logger.LogInformation("Launched Chromium {Version}.", _browser.Version);

            return _browser;
        }
        finally
        {
            _browserLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.CloseAsync();
        }

        _playwright?.Dispose();
        _concurrency.Dispose();
        _browserLock.Dispose();
    }
}

public sealed class RendererOptions
{
    public const string SectionName = "Renderer";

    /// <summary>
    /// Templates are authored at half size and screenshotted at 2x, so CSS pixel values stay
    /// readable while output is retina.
    /// </summary>
    public float DeviceScaleFactor { get; set; } = 2;

    /// <summary>Rendering is CPU-bound; let the queue back up rather than thrash.</summary>
    public int MaxConcurrentRenders { get; set; } = Math.Max(1, Environment.ProcessorCount / 2);

    public TimeSpan RenderTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public int RecycleBrowserAfterRenders { get; set; } = 250;

    public string FontDirectory { get; set; } = "Fonts";

    public string TemplateDirectory { get; set; } = "Templates";

    /// <summary>Refuses payloads that would blow out memory before Chromium ever sees them.</summary>
    public int MaxAssetBytes { get; set; } = 12 * 1024 * 1024;
}
