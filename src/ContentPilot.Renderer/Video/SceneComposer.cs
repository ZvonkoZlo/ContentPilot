using ContentPilot.Rendering.Contracts;

namespace ContentPilot.Renderer.Video;

/// <summary>Materialises rendered layers and invokes one FFmpeg composition pass.</summary>
public sealed class SceneComposer(FfmpegRunner ffmpeg)
{
    public async Task<string> ComposeAsync(
        ReelSpec spec,
        ReelTemplateManifest manifest,
        IReadOnlyList<RenderedSceneLayers> layers,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        if (layers.Count != spec.Scenes.Count)
        {
            throw new ReelRenderException(
                $"Expected {spec.Scenes.Count} rendered scenes, but received {layers.Count}.");
        }

        var files = new List<SceneLayerFiles>(layers.Count);
        var writes = new List<Task>(layers.Count * 3);

        for (var index = 0; index < layers.Count; index++)
        {
            var background = Path.Combine(workingDirectory, $"scene-{index:D2}-background.png");
            var product = Path.Combine(workingDirectory, $"scene-{index:D2}-product.png");
            var text = Path.Combine(workingDirectory, $"scene-{index:D2}-text.png");

            writes.Add(File.WriteAllBytesAsync(background, layers[index].BackgroundPng, cancellationToken));
            writes.Add(File.WriteAllBytesAsync(product, layers[index].ProductPng, cancellationToken));
            writes.Add(File.WriteAllBytesAsync(text, layers[index].TextPng, cancellationToken));
            files.Add(new SceneLayerFiles(background, product, text));
        }

        string? musicPath = null;

        if (spec.Music is not null)
        {
            musicPath = Path.Combine(workingDirectory, $"music{AudioExtension(spec.Music.MediaType)}");
            writes.Add(File.WriteAllBytesAsync(musicPath, spec.Music.ToBytes(), cancellationToken));
        }

        await Task.WhenAll(writes);

        var outputPath = Path.Combine(workingDirectory, "reel.mp4");
        await ffmpeg.ComposeAsync(spec, manifest, files, musicPath, outputPath, cancellationToken);

        return outputPath;
    }

    private static string AudioExtension(string mediaType) => mediaType.ToLowerInvariant() switch
    {
        "audio/aac" => ".aac",
        "audio/m4a" or "audio/mp4" => ".m4a",
        "audio/mpeg" => ".mp3",
        "audio/ogg" => ".ogg",
        _ => ".wav",
    };
}
