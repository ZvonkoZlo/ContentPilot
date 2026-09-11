using ContentPilot.Rendering.Contracts;
using ContentPilot.Renderer.Imaging;
using ImageMagick;

namespace ContentPilot.Renderer.Video;

public sealed class ReelKeyframeExtractor(FfmpegRunner ffmpeg)
{
    public async Task<IReadOnlyList<ReelKeyframe>> ExtractAsync(
        string videoPath,
        ReelSpec spec,
        ReelTemplateManifest manifest,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<double> timestamps = spec.Options.IncludeKeyframes
            ? Timeline.KeyframeTimestamps(spec.Scenes, manifest.Scenes)
            : [0];
        var images = await ffmpeg.ExtractFramesAsync(videoPath, timestamps, workingDirectory, cancellationToken);
        var keyframes = new ReelKeyframe[images.Count];

        Parallel.For(0, images.Count, new ParallelOptions { CancellationToken = cancellationToken }, index =>
        {
            using var image = new MagickImage(images[index]);
            keyframes[index] = new ReelKeyframe
            {
                TimestampSeconds = timestamps[index],
                Kind = KindFor(timestamps[index], spec, manifest),
                Image = ImagePayload.FromBytes(images[index], "image/png"),
                BlackPixelFraction = BlackPixelFraction(image),
                PerceptualHash = ContentPilot.Renderer.Imaging.PerceptualHash.Compute(image),
            };
        });

        return keyframes;
    }

    private static ReelKeyframeKind KindFor(
        double timestamp,
        ReelSpec spec,
        ReelTemplateManifest manifest)
    {
        if (timestamp == 0)
        {
            return ReelKeyframeKind.Cover;
        }

        var start = 0.0;

        for (var index = 0; index < spec.Scenes.Count; index++)
        {
            var duration = spec.Scenes[index].DurationSeconds;

            if (Close(timestamp, start + (duration / 2)))
            {
                return ReelKeyframeKind.SceneMidpoint;
            }

            if (index < spec.Scenes.Count - 1)
            {
                var transitionStart = start + duration - manifest.Scenes[index].TransitionSeconds;

                if (Close(timestamp, transitionStart))
                {
                    return ReelKeyframeKind.TransitionStart;
                }

                if (Close(timestamp, start + duration))
                {
                    return ReelKeyframeKind.TransitionEnd;
                }

                start = transitionStart;
            }
        }

        return ReelKeyframeKind.SceneMidpoint;
    }

    private static double BlackPixelFraction(IMagickImage<ushort> image)
    {
        using var pixels = image.GetPixels();
        var stepX = Math.Max(1, (int)image.Width / 64);
        var stepY = Math.Max(1, (int)image.Height / 64);
        var black = 0;
        var count = 0;

        for (var y = 0; y < image.Height; y += stepY)
        {
            for (var x = 0; x < image.Width; x += stepX)
            {
                var pixel = pixels.GetPixel(x, y);
                var luminance = (0.2126 * pixel.GetChannel(0) +
                                 0.7152 * pixel.GetChannel(1) +
                                 0.0722 * pixel.GetChannel(2)) / ushort.MaxValue;

                if (luminance < 0.02)
                {
                    black++;
                }

                count++;
            }
        }

        return count == 0 ? 1 : (double)black / count;
    }

    private static bool Close(double left, double right) => Math.Abs(left - right) < 0.002;
}
