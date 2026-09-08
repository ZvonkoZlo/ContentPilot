using System.Diagnostics;
using System.Text.Json;
using ContentPilot.Rendering.Contracts;
using ContentPilot.Renderer.Imaging;
using ContentPilot.Renderer.Templates;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace ContentPilot.Renderer.Engine;

/// <summary>
/// One render: template component to HTML, HTML to a screenshot, and a measurement report
/// read out of the layout engine rather than inferred from pixels.
/// </summary>
public sealed class ImageRenderService(
    TemplateCatalog catalog,
    DocumentBuilder documentBuilder,
    BrowserPool browsers,
    FontLibrary fonts,
    ContrastAnalyzer contrast,
    IServiceProvider services,
    ILoggerFactory loggerFactory,
    IOptions<RendererOptions> options,
    ILogger<ImageRenderService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly RendererOptions _options = options.Value;

    public async Task<RenderImageResponse> RenderAsync(RenderImageRequest request, CancellationToken ct)
    {
        var entry = catalog.Get(request.TemplateId);
        var manifest = entry.Manifest;

        Validate(request, manifest);

        var stopwatch = Stopwatch.StartNew();
        var html = await BuildHtmlAsync(request, entry);

        return await browsers.UsePageAsync(async (page, blocked) =>
        {
            await page.SetContentAsync(html, new PageSetContentOptions { WaitUntil = WaitUntilState.Load });

            var painted = await page.EvaluateAsync<bool>(RenderScripts.WaitForPaint);

            if (!painted)
            {
                // A half-decoded image would silently produce a broken frame that looks
                // plausible enough for QA to pass.
                throw new RenderFailedException("One or more images failed to decode before the screenshot.");
            }

            await ApplyShrinkToFitAsync(page, manifest);

            var frame = await page.QuerySelectorAsync($"#{DocumentBuilder.FrameElementId}")
                ?? throw new RenderFailedException("The document produced no frame element.");

            // Passed as a double: a float argument does not survive the Playwright
            // bridge as a JS number, and every measurement silently becomes zero.
            var reportJson = await page.EvaluateAsync<JsonElement>(
                RenderScripts.CollectReport, (double)_options.DeviceScaleFactor);

            var imageBytes = await frame.ScreenshotAsync(new ElementHandleScreenshotOptions
            {
                Type = request.Options.Format == ImageFormat.Jpeg ? ScreenshotType.Jpeg : ScreenshotType.Png,
                Quality = request.Options.Format == ImageFormat.Jpeg ? request.Options.JpegQuality : null,
                Animations = ScreenshotAnimations.Disabled,
                Scale = ScreenshotScale.Device,
            });

            var masks = request.Options.IncludeMasks
                ? await RenderMasksAsync(page, frame, manifest, ct)
                : new Dictionary<string, MaskRender>();

            stopwatch.Stop();

            var report = BuildReport(reportJson, masks, imageBytes, blocked, stopwatch.ElapsedMilliseconds, request);

            logger.LogInformation(
                "Rendered {TemplateId} v{Version} at {Ratio} in {Duration} ms ({Bytes} bytes).",
                manifest.TemplateId, manifest.Version, request.AspectRatio, stopwatch.ElapsedMilliseconds, imageBytes.Length);

            return new RenderImageResponse
            {
                TemplateId = manifest.TemplateId,
                TemplateVersion = manifest.Version,
                Image = ImagePayload.FromBytes(imageBytes, MediaTypeFor(request.Options.Format)),
                Masks = masks.ToDictionary(
                    m => m.Key,
                    m => ImagePayload.FromBytes(m.Value.Png, "image/png"),
                    StringComparer.Ordinal),
                Report = report,
            };
        }, ct);
    }

    /// <summary>
    /// Renders the component to an HTML fragment. Blazor static rendering runs on its own
    /// dispatcher, so the call is marshalled rather than awaited directly.
    /// </summary>
    private async Task<string> BuildHtmlAsync(RenderImageRequest request, TemplateEntry entry)
    {
        await using var renderer = new HtmlRenderer(services, loggerFactory);

        var model = new TemplateModel(entry.Manifest, request);

        var fragment = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?> { ["Model"] = model });
            var output = await renderer.RenderComponentAsync(entry.ComponentType, parameters);

            return output.ToHtmlString();
        });

        return documentBuilder.Build(request, entry.Manifest, fragment);
    }

    private static async Task ApplyShrinkToFitAsync(IPage page, TemplateManifest manifest)
    {
        var config = new
        {
            slots = manifest.TextSlots
                .Where(s => s.ShrinkToFit)
                .Select(s => new { id = s.Id, shrinkToFit = true, minFontPx = s.MinFontPx })
                .ToArray(),
        };

        if (config.slots.Length == 0)
        {
            return;
        }

        await page.EvaluateAsync(RenderScripts.ShrinkToFit, config);
    }

    /// <summary>
    /// One extra pass per immutable slot. The page is already loaded, so it costs little,
    /// and it yields the slot box and its occlusion exactly instead of by inference.
    /// </summary>
    private async Task<Dictionary<string, MaskRender>> RenderMasksAsync(
        IPage page, IElementHandle frame, TemplateManifest manifest, CancellationToken ct)
    {
        var masks = new Dictionary<string, MaskRender>(StringComparer.Ordinal);

        foreach (var slot in manifest.AssetSlots.Where(s => s.Immutable))
        {
            ct.ThrowIfCancellationRequested();

            var box = await page.EvaluateAsync<JsonElement?>(RenderScripts.ApplyMask, slot.Id);

            if (box is null || box.Value.ValueKind == JsonValueKind.Null)
            {
                // Optional slot the template chose not to render. Nothing to verify.
                await page.EvaluateAsync(RenderScripts.RemoveMask);
                continue;
            }

            var png = await frame.ScreenshotAsync(new ElementHandleScreenshotOptions
            {
                Type = ScreenshotType.Png,
                Animations = ScreenshotAnimations.Disabled,
                Scale = ScreenshotScale.Device,
            });

            await page.EvaluateAsync(RenderScripts.RemoveMask);

            masks[slot.Id] = new MaskRender(png);
        }

        return masks;
    }

    private RenderReport BuildReport(
        JsonElement raw,
        IReadOnlyDictionary<string, MaskRender> masks,
        byte[] imageBytes,
        IReadOnlyList<string> blocked,
        long durationMs,
        RenderImageRequest request)
    {
        var measurements = new List<SlotMeasurement>();

        using var image = MaskAnalyzer.Load(imageBytes);

        foreach (var slot in raw.GetProperty("slots").EnumerateArray())
        {
            var slotId = slot.GetProperty("slotId").GetString()!;
            var box = JsonSerializer.Deserialize<BoundingBox>(slot.GetProperty("box").GetRawText(), JsonOptions)!;
            var isText = slot.GetProperty("kind").GetString() == "text";

            var occlusion = masks.TryGetValue(slotId, out var mask)
                ? MaskAnalyzer.MeasureOcclusion(mask.Png, box)
                : 0;

            double? luminance = null;
            double? ratio = null;

            if (isText && slot.TryGetProperty("foregroundColor", out var fg) && fg.ValueKind == JsonValueKind.String)
            {
                (luminance, ratio) = contrast.Measure(image, box, fg.GetString()!);
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

        var fontsLoaded = raw.GetProperty("fontsLoaded").EnumerateArray()
            .Select(f => f.GetString() ?? string.Empty)
            .Where(f => f.Length > 0)
            .ToArray();

        return new RenderReport
        {
            Width = raw.GetProperty("width").GetInt32(),
            Height = raw.GetProperty("height").GetInt32(),
            DeviceScaleFactor = _options.DeviceScaleFactor,
            Slots = measurements,
            FontsLoaded = fontsLoaded,
            RenderDurationMs = durationMs,
            BlockedRequests = blocked.Distinct(StringComparer.Ordinal).ToArray(),
        };
    }

    /// <summary>
    /// Rejects a request the template cannot satisfy before Chromium is ever started.
    /// Failing here is cheap, specific, and traceable to a slot.
    /// </summary>
    private void Validate(RenderImageRequest request, TemplateManifest manifest)
    {
        if (request.TemplateVersion is { } pinned && pinned != manifest.Version)
        {
            throw new RenderFailedException(
                $"Template '{manifest.TemplateId}' is at version {manifest.Version}; the request pinned {pinned}. " +
                "Re-run the creative director rather than rendering a spec against a template it never saw.");
        }

        if (!manifest.Supports(request.AspectRatio))
        {
            throw new RenderFailedException(
                $"Template '{manifest.TemplateId}' does not support {request.AspectRatio}.");
        }

        if (!manifest.ColorSchemes.Contains(request.ColorScheme))
        {
            throw new RenderFailedException(
                $"Template '{manifest.TemplateId}' does not offer the {request.ColorScheme} scheme.");
        }

        foreach (var family in new[] { request.Brand.HeadingFont, request.Brand.BodyFont })
        {
            if (!fonts.Has(family))
            {
                throw new RenderFailedException(
                    $"Font '{family}' is not embedded in this renderer. Available: {string.Join(", ", fonts.Families)}.");
            }
        }

        foreach (var slot in manifest.TextSlots.Where(s => s.Required))
        {
            if (!request.Text.TryGetValue(slot.Id, out var value) || string.IsNullOrWhiteSpace(value))
            {
                throw new RenderFailedException($"Required text slot '{slot.Id}' is empty.");
            }
        }

        foreach (var (slotId, value) in request.Text)
        {
            var slot = manifest.FindTextSlot(slotId);

            if (slot is null)
            {
                throw new RenderFailedException($"Template '{manifest.TemplateId}' has no text slot '{slotId}'.");
            }

            // The budget is the contract the copywriter was briefed against. Silently
            // rendering an over-budget string is how clipped headlines reach production.
            var budget = slot.BudgetFor(request.Language);

            if (value.Length > budget)
            {
                throw new RenderFailedException(
                    $"Slot '{slotId}' carries {value.Length} characters; the budget for '{request.Language}' is {budget}.");
            }
        }

        foreach (var slot in manifest.AssetSlots.Where(s => s.Required))
        {
            if (!request.Assets.ContainsKey(slot.Id))
            {
                throw new RenderFailedException($"Required asset slot '{slot.Id}' is empty.");
            }
        }

        foreach (var (slotId, payload) in request.Assets)
        {
            if (manifest.FindAssetSlot(slotId) is null)
            {
                throw new RenderFailedException($"Template '{manifest.TemplateId}' has no asset slot '{slotId}'.");
            }

            // Base64 inflates by 4/3; compare against the decoded size.
            var approximateBytes = payload.Base64.Length / 4 * 3;

            if (approximateBytes > _options.MaxAssetBytes)
            {
                throw new RenderFailedException(
                    $"Asset '{slotId}' is roughly {approximateBytes / 1024 / 1024} MB; the ceiling is " +
                    $"{_options.MaxAssetBytes / 1024 / 1024} MB.");
            }
        }
    }

    private static string MediaTypeFor(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => "image/jpeg",
        ImageFormat.Webp => "image/webp",
        _ => "image/png",
    };

    private readonly record struct MaskRender(byte[] Png);
}

public sealed class RenderFailedException(string message) : Exception(message);
