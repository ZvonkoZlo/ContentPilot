using System.Diagnostics;
using ContentPilot.Rendering.Contracts;
using ContentPilot.Renderer.Engine;
using Microsoft.Extensions.Options;

namespace ContentPilot.Renderer.Video;

/// <summary>The Phase 7 Playwright-layer plus FFmpeg implementation of <see cref="IReelRenderer"/>.</summary>
public sealed class FfmpegReelRenderer(
    ReelTemplateCatalog catalog,
    ReelLayerRenderer layerRenderer,
    SceneComposer composer,
    FfmpegRunner ffmpeg,
    ReelKeyframeExtractor keyframes,
    ReelQaService qa,
    FontLibrary fonts,
    IOptions<RendererOptions> rendererOptions,
    ILogger<FfmpegReelRenderer> logger) : IReelRenderer
{
    public async Task<RenderReelResponse> RenderAsync(ReelSpec spec, CancellationToken cancellationToken)
    {
        var manifest = catalog.Get(spec.TemplateId);
        ReelSpecValidator.Validate(spec, manifest, rendererOptions.Value.MaxAssetBytes);
        ValidateFonts(spec);

        var stopwatch = Stopwatch.StartNew();
        var workspace = Directory.CreateTempSubdirectory("contentpilot-reel-");

        try
        {
            var layers = await layerRenderer.RenderAsync(spec, manifest, cancellationToken);
            var videoPath = await composer.ComposeAsync(
                spec, manifest, layers, workspace.FullName, cancellationToken);
            var probeTask = ffmpeg.InspectAsync(videoPath, cancellationToken);
            var keyframeTask = keyframes.ExtractAsync(
                videoPath, spec, manifest, workspace.FullName, cancellationToken);

            await Task.WhenAll(probeTask, keyframeTask);

            var probe = await probeTask;
            var extracted = await keyframeTask;

            stopwatch.Stop();

            var report = qa.BuildReport(spec, manifest, layers, extracted, probe, stopwatch.ElapsedMilliseconds);
            var cover = extracted.FirstOrDefault(frame => frame.Kind == ReelKeyframeKind.Cover)?.Image
                ?? throw new ReelRenderException("FFmpeg did not produce a cover frame.");
            var video = await File.ReadAllBytesAsync(videoPath, cancellationToken);

            logger.LogInformation(
                "Rendered reel {TemplateId} v{Version} in {Duration} ms ({Bytes} bytes, QA {Qa}).",
                manifest.TemplateId,
                manifest.Version,
                stopwatch.ElapsedMilliseconds,
                video.Length,
                report.Passed ? "passed" : "failed");

            return new RenderReelResponse
            {
                TemplateId = manifest.TemplateId,
                TemplateVersion = manifest.Version,
                Video = BinaryPayload.FromBytes(video, "video/mp4"),
                Cover = cover,
                Keyframes = extracted,
                Report = report,
            };
        }
        finally
        {
            try
            {
                workspace.Delete(recursive: true);
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Could not delete reel workspace {Workspace}.", workspace.FullName);
            }
            catch (UnauthorizedAccessException ex)
            {
                logger.LogWarning(ex, "Could not delete reel workspace {Workspace}.", workspace.FullName);
            }
        }
    }

    private void ValidateFonts(ReelSpec spec)
    {
        foreach (var family in new[] { spec.Brand.HeadingFont, spec.Brand.BodyFont })
        {
            if (!fonts.Has(family))
            {
                throw new ReelRenderException(
                    $"Font '{family}' is not embedded in this renderer. Available: {string.Join(", ", fonts.Families)}.");
            }
        }
    }
}
