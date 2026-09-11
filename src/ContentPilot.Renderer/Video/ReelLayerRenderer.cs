using System.Diagnostics;
using System.Text.Json;
using ContentPilot.Rendering.Contracts;
using ContentPilot.Renderer.Engine;
using ContentPilot.Renderer.Imaging;
using ContentPilot.Renderer.Templates.Reel;
using ImageMagick;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace ContentPilot.Renderer.Video;

public sealed record RenderedSceneLayers(
    string SceneId,
    byte[] BackgroundPng,
    byte[] ProductPng,
    byte[] TextPng,
    byte[] CompositePng,
    RenderReport Report);

/// <summary>Renders every scene layer through one isolated Playwright page per scene.</summary>
public sealed class ReelLayerRenderer(
    BrowserPool browsers,
    ReelDocumentBuilder documents,
    ContrastAnalyzer contrast,
    IServiceProvider services,
    ILoggerFactory loggerFactory,
    IOptions<RendererOptions> options,
    ILogger<ReelLayerRenderer> logger)
{
    private const string BackgroundOnlyCss =
        ".reel-product,.reel-copy,.reel-logo{display:none!important}";

    private const string ProductOnlyCss =
        "html,body,#frame{background:transparent!important}" +
        ".reel-background,.reel-orb,.reel-grid,.reel-copy,.reel-logo{display:none!important}";

    private const string TextOnlyCss =
        "html,body,#frame{background:transparent!important}" +
        ".reel-background,.reel-orb,.reel-grid,.reel-product{display:none!important}";

    private static readonly Lazy<byte[]> TransparentLayer = new(CreateTransparentLayer);
    private readonly RendererOptions _options = options.Value;

    public async Task<IReadOnlyList<RenderedSceneLayers>> RenderAsync(
        ReelSpec spec,
        ReelTemplateManifest manifest,
        CancellationToken cancellationToken)
    {
        var tasks = spec.Scenes.Select((scene, index) =>
            RenderSceneAsync(spec, scene, manifest, manifest.Scenes[index], cancellationToken));

        return await Task.WhenAll(tasks);
    }

    private async Task<RenderedSceneLayers> RenderSceneAsync(
        ReelSpec spec,
        ReelSceneSpec scene,
        ReelTemplateManifest manifest,
        ReelSceneManifest declared,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var component = ReelTemplateComponentRegistry.Get(manifest.TemplateId);

        var html = await BuildHtmlAsync(spec, scene, manifest, declared, component, ReelLayer.Composite);

        return await browsers.UsePageAsync(async (page, blocked) =>
        {
            await LoadAsync(page, html, declared);

            var background = await ScreenshotLayerAsync(page, BackgroundOnlyCss, transparent: false);
            var product = HasProductLayer(scene, declared)
                ? await ScreenshotPositionedLayerAsync(page, ProductOnlyCss, ".reel-product")
                : TransparentLayer.Value;
            var text = await ScreenshotPositionedLayerAsync(page, TextOnlyCss, ".reel-copy", ".reel-logo");
            var composite = Composite(background, product, text);

            var reportJson = await page.EvaluateAsync<JsonElement>(
                RenderScripts.CollectReport, (double)_options.DeviceScaleFactor);
            var masks = await RenderMasksAsync(page, declared, cancellationToken);

            stopwatch.Stop();

            var report = BuildReport(reportJson, masks, composite, blocked, stopwatch.ElapsedMilliseconds);

            logger.LogInformation(
                "Rendered reel scene {SceneId} as three layers in {Duration} ms.",
                scene.SceneId,
                stopwatch.ElapsedMilliseconds);

            return new RenderedSceneLayers(scene.SceneId, background, product, text, composite, report);
        }, cancellationToken);
    }

    private async Task<string> BuildHtmlAsync(
        ReelSpec spec,
        ReelSceneSpec scene,
        ReelTemplateManifest manifest,
        ReelSceneManifest declared,
        Type component,
        ReelLayer layer)
    {
        await using var renderer = new HtmlRenderer(services, loggerFactory);
        var model = new ReelLayerModel(manifest, declared, spec, scene, layer);

        var fragment = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = ParameterView.FromDictionary(
                new Dictionary<string, object?> { ["Model"] = model });
            var output = await renderer.RenderComponentAsync(component, parameters);
            return output.ToHtmlString();
        });

        return documents.Build(spec, manifest, fragment, layer is ReelLayer.Product or ReelLayer.Text);
    }

    private static async Task LoadAsync(
        IPage page,
        string html,
        ReelSceneManifest scene)
    {
        await page.SetContentAsync(html, new PageSetContentOptions { WaitUntil = WaitUntilState.Load });

        if (!await page.EvaluateAsync<bool>(RenderScripts.WaitForPaint))
        {
            throw new ReelRenderException($"One or more images failed to decode in scene '{scene.Id}'.");
        }

        var shrink = scene.TextSlots
            .Where(slot => slot.ShrinkToFit)
            .Select(slot => new { id = slot.Id, shrinkToFit = true, minFontPx = slot.MinFontPx })
            .ToArray();

        if (shrink.Length > 0)
        {
            await page.EvaluateAsync(RenderScripts.ShrinkToFit, new { slots = shrink });
        }
    }

    private static async Task<byte[]> ScreenshotLayerAsync(IPage page, string css, bool transparent)
    {
        await page.EvaluateAsync(
            "css => { const style = document.createElement('style'); style.id = 'cp-reel-layer'; style.textContent = css; document.head.appendChild(style); }",
            css);

        try
        {
            var frame = await page.QuerySelectorAsync($"#{DocumentBuilder.FrameElementId}")
                ?? throw new ReelRenderException("A reel layer produced no frame element.");

            return await frame.ScreenshotAsync(new ElementHandleScreenshotOptions
            {
                Type = ScreenshotType.Png,
                Animations = ScreenshotAnimations.Disabled,
                Scale = ScreenshotScale.Device,
                OmitBackground = transparent,
            });
        }
        finally
        {
            await page.EvaluateAsync("() => document.getElementById('cp-reel-layer')?.remove()");
        }
    }

    private async Task<byte[]> ScreenshotPositionedLayerAsync(
        IPage page,
        string css,
        params string[] selectors)
    {
        await page.EvaluateAsync(
            "css => { const style = document.createElement('style'); style.id = 'cp-reel-layer'; style.textContent = css; document.head.appendChild(style); }",
            css);

        try
        {
            var frame = await page.QuerySelectorAsync($"#{DocumentBuilder.FrameElementId}")
                ?? throw new ReelRenderException("A reel layer produced no frame element.");
            var frameBox = await frame.BoundingBoxAsync()
                ?? throw new ReelRenderException("A reel layer frame has no layout box.");
            using var canvas = new MagickImage(MagickColors.Transparent, 1080, 1920);

            foreach (var selector in selectors)
            {
                foreach (var element in await page.QuerySelectorAllAsync(selector))
                {
                    var box = await element.BoundingBoxAsync();

                    if (box is null || box.Width <= 0 || box.Height <= 0)
                    {
                        continue;
                    }

                    var png = await element.ScreenshotAsync(new ElementHandleScreenshotOptions
                    {
                        Type = ScreenshotType.Png,
                        Animations = ScreenshotAnimations.Disabled,
                        Scale = ScreenshotScale.Device,
                        OmitBackground = true,
                    });
                    using var overlay = new MagickImage(png);
                    var x = (int)Math.Round((box.X - frameBox.X) * _options.DeviceScaleFactor);
                    var y = (int)Math.Round((box.Y - frameBox.Y) * _options.DeviceScaleFactor);
                    canvas.Composite(overlay, x, y, CompositeOperator.Over);
                }
            }

            canvas.Settings.SetDefine(MagickFormat.Png, "compression-level", "1");
            return canvas.ToByteArray(MagickFormat.Png);
        }
        finally
        {
            await page.EvaluateAsync("() => document.getElementById('cp-reel-layer')?.remove()");
        }
    }

    private static bool HasProductLayer(ReelSceneSpec scene, ReelSceneManifest declared) =>
        declared.AssetSlots.Any(slot =>
            slot.Kind is AssetKind.ProductScreenshot or AssetKind.Photo && scene.Assets.ContainsKey(slot.Id));

    private static byte[] CreateTransparentLayer()
    {
        using var image = new MagickImage(MagickColors.Transparent, 1080, 1920);
        image.Settings.SetDefine(MagickFormat.Png, "compression-level", "1");
        return image.ToByteArray(MagickFormat.Png);
    }

    private static byte[] Composite(params byte[][] layers)
    {
        using var image = new MagickImage(layers[0]);

        foreach (var layer in layers.Skip(1))
        {
            using var overlay = new MagickImage(layer);
            image.Composite(overlay, CompositeOperator.Over);
        }

        image.Settings.SetDefine(MagickFormat.Png, "compression-level", "1");
        return image.ToByteArray(MagickFormat.Png);
    }

    private static async Task<Dictionary<string, byte[]>> RenderMasksAsync(
        IPage page,
        ReelSceneManifest scene,
        CancellationToken cancellationToken)
    {
        var masks = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var frame = await page.QuerySelectorAsync($"#{DocumentBuilder.FrameElementId}")
            ?? throw new ReelRenderException("A reel composite produced no frame element.");

        foreach (var slot in scene.AssetSlots.Where(s => s.Immutable))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var box = await page.EvaluateAsync<JsonElement?>(RenderScripts.ApplyMask, slot.Id);

            if (box is null || box.Value.ValueKind == JsonValueKind.Null)
            {
                await page.EvaluateAsync(RenderScripts.RemoveMask);
                continue;
            }

            masks[slot.Id] = await frame.ScreenshotAsync(new ElementHandleScreenshotOptions
            {
                Type = ScreenshotType.Png,
                Animations = ScreenshotAnimations.Disabled,
                Scale = ScreenshotScale.Css,
            });

            await page.EvaluateAsync(RenderScripts.RemoveMask);
        }

        return masks;
    }

    private RenderReport BuildReport(
        JsonElement raw,
        IReadOnlyDictionary<string, byte[]> masks,
        byte[] compositePng,
        IReadOnlyList<string> blocked,
        long durationMs)
    {
        using var image = new MagickImage(compositePng);
        var measurements = new List<SlotMeasurement>();

        foreach (var slot in raw.GetProperty("slots").EnumerateArray())
        {
            var slotId = slot.GetProperty("slotId").GetString() ?? string.Empty;
            var boxJson = slot.GetProperty("box");
            var box = new BoundingBox
            {
                X = boxJson.GetProperty("x").GetDouble(),
                Y = boxJson.GetProperty("y").GetDouble(),
                Width = boxJson.GetProperty("width").GetDouble(),
                Height = boxJson.GetProperty("height").GetDouble(),
            };
            var isText = slot.GetProperty("kind").GetString() == "text";
            var maskBox = new BoundingBox
            {
                X = box.X / _options.DeviceScaleFactor,
                Y = box.Y / _options.DeviceScaleFactor,
                Width = box.Width / _options.DeviceScaleFactor,
                Height = box.Height / _options.DeviceScaleFactor,
            };
            var occlusion = masks.TryGetValue(slotId, out var mask)
                ? MaskAnalyzer.MeasureOcclusion(mask, maskBox)
                : 0;
            double? luminance = null;
            double? ratio = null;

            if (isText && slot.TryGetProperty("foregroundColor", out var foreground) &&
                foreground.ValueKind == JsonValueKind.String)
            {
                (luminance, ratio) = contrast.Measure(image, box, foreground.GetString()!);
            }

            measurements.Add(new SlotMeasurement
            {
                SlotId = slotId,
                Box = box,
                Overflows = slot.GetProperty("overflows").GetBoolean(),
                LineCount = slot.GetProperty("lineCount").GetInt32(),
                FontSizePx = slot.GetProperty("fontSizePx").GetDouble(),
                ShrinkApplied = slot.GetProperty("shrinkApplied").GetBoolean(),
                ForegroundColor = isText ? slot.GetProperty("foregroundColor").GetString() : null,
                BackdropLuminance = luminance,
                ContrastRatio = ratio,
                Occlusion = occlusion,
                BreaksSafeArea = slot.GetProperty("breaksSafeArea").GetBoolean(),
            });
        }

        return new RenderReport
        {
            Width = raw.GetProperty("width").GetInt32(),
            Height = raw.GetProperty("height").GetInt32(),
            DeviceScaleFactor = _options.DeviceScaleFactor,
            Slots = measurements,
            FontsLoaded = raw.GetProperty("fontsLoaded").EnumerateArray()
                .Select(value => value.GetString() ?? string.Empty)
                .Where(value => value.Length > 0)
                .ToArray(),
            RenderDurationMs = durationMs,
            BlockedRequests = blocked.Distinct(StringComparer.Ordinal).ToArray(),
        };
    }
}
