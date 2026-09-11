using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ContentPilot.Rendering.Contracts;

namespace ContentPilot.Renderer.Video;

public sealed class ReelRendererOptions
{
    public string FfmpegPath { get; set; } = "ffmpeg";

    public string FfprobePath { get; set; } = "ffprobe";

    public TimeSpan ProcessTimeout { get; set; } = TimeSpan.FromSeconds(45);
}

public sealed record VideoProbe(
    int Width,
    int Height,
    double DurationSeconds,
    double FramesPerSecond,
    bool HasAudio,
    double? AudioPeakDbfs,
    int BlackFrameCount);

public sealed class FfmpegRunner(ReelRendererOptions options, ILogger<FfmpegRunner> logger)
{
    private static readonly Regex AudioPeakPattern = new(
        @"max_volume:\s*(?<peak>-?(?:\d+(?:\.\d+)?|inf))\s*dB",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex BlackFramePattern = new(
        @"black_start:", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task ComposeAsync(
        ReelSpec spec,
        ReelTemplateManifest manifest,
        IReadOnlyList<SceneLayerFiles> scenes,
        string? musicPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var plan = FfmpegFiltergraphBuilder.Build(spec, manifest);
        var arguments = new List<string> { "-y", "-hide_banner", "-loglevel", "error" };

        for (var index = 0; index < scenes.Count; index++)
        {
            foreach (var path in new[] { scenes[index].BackgroundPath, scenes[index].ProductPath, scenes[index].TextPath })
            {
                arguments.AddRange(["-i", path]);
            }
        }

        if (musicPath is not null)
        {
            arguments.AddRange(["-stream_loop", "-1", "-i", musicPath]);
        }

        arguments.AddRange([
            "-filter_complex", plan.Filtergraph,
            "-map", $"[{plan.VideoOutputLabel}]",
            "-map", $"[{plan.AudioOutputLabel}]",
            "-c:v", "libx264",
            "-preset", "ultrafast",
            "-crf", spec.Options.ConstantRateFactor.ToString(CultureInfo.InvariantCulture),
            "-r", spec.Options.FramesPerSecond.ToString(CultureInfo.InvariantCulture),
            "-pix_fmt", "yuv420p",
            "-c:a", "aac",
            "-b:a", "160k",
            "-ar", "48000",
            "-movflags", "+faststart",
            "-t", Num(plan.DurationSeconds),
            "-shortest",
            outputPath,
        ]);

        await RunAsync(options.FfmpegPath, arguments, "FFmpeg composition", cancellationToken);
    }

    public async Task<IReadOnlyList<byte[]>> ExtractFramesAsync(
        string videoPath,
        IReadOnlyList<double> timestamps,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        if (timestamps.Count == 0)
        {
            return [];
        }

        var splitLabels = string.Concat(Enumerable.Range(0, timestamps.Count).Select(index => $"[split{index}]"));
        var graph = new StringBuilder($"[0:v]split={timestamps.Count}{splitLabels};");

        for (var index = 0; index < timestamps.Count; index++)
        {
            var start = Math.Max(0, timestamps[index]);
            var end = start + (1.0 / 30.0);

            graph.Append("[split").Append(index).Append("]trim=start=").Append(Num(start))
                .Append(":end=").Append(Num(end)).Append(",setpts=PTS-STARTPTS[out")
                .Append(index).Append(']');

            if (index < timestamps.Count - 1)
            {
                graph.Append(';');
            }
        }

        var arguments = new List<string>
        {
            "-y", "-hide_banner", "-loglevel", "error", "-i", videoPath, "-filter_complex", graph.ToString(),
        };
        var paths = new List<string>(timestamps.Count);

        for (var index = 0; index < timestamps.Count; index++)
        {
            var path = Path.Combine(outputDirectory, $"keyframe-{index:D2}.png");
            paths.Add(path);
            arguments.AddRange(["-map", $"[out{index}]", "-frames:v", "1", path]);
        }

        await RunAsync(options.FfmpegPath, arguments, "FFmpeg keyframe extraction", cancellationToken);

        return await Task.WhenAll(paths.Select(path => File.ReadAllBytesAsync(path, cancellationToken)));
    }

    public async Task<VideoProbe> InspectAsync(string videoPath, CancellationToken cancellationToken)
    {
        var probe = await RunAsync(
            options.FfprobePath,
            ["-v", "error", "-show_streams", "-show_format", "-of", "json", videoPath],
            "ffprobe inspection",
            cancellationToken);

        using var document = JsonDocument.Parse(probe.StandardOutput);
        var streams = document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.FirstOrDefault(stream => stream.GetProperty("codec_type").GetString() == "video");

        if (video.ValueKind == JsonValueKind.Undefined)
        {
            throw new ReelRenderException("The composed reel has no video stream.");
        }

        var format = document.RootElement.GetProperty("format");
        var duration = ParseDouble(format.GetProperty("duration").GetString());
        var frameRate = ParseFrameRate(video.GetProperty("avg_frame_rate").GetString());
        var hasAudio = streams.Any(stream => stream.GetProperty("codec_type").GetString() == "audio");

        var analysis = await AnalyzeAsync(videoPath, hasAudio, cancellationToken);

        return new VideoProbe(
            video.GetProperty("width").GetInt32(),
            video.GetProperty("height").GetInt32(),
            duration,
            frameRate,
            hasAudio,
            analysis.AudioPeakDbfs,
            analysis.BlackFrameCount);
    }

    private async Task<(double? AudioPeakDbfs, int BlackFrameCount)> AnalyzeAsync(
        string videoPath,
        bool hasAudio,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            options.FfmpegPath,
            hasAudio
                ? ["-hide_banner", "-i", videoPath, "-vf", "blackdetect=d=0.08:pix_th=0.10", "-af", "volumedetect", "-f", "null", "-"]
                : ["-hide_banner", "-i", videoPath, "-vf", "blackdetect=d=0.08:pix_th=0.10", "-an", "-f", "null", "-"],
            "FFmpeg container analysis",
            cancellationToken,
            acceptExitCodeOne: false);
        var match = AudioPeakPattern.Match(result.StandardError);
        double? audioPeak = null;

        if (match.Success && !match.Groups["peak"].Value.Equals("-inf", StringComparison.OrdinalIgnoreCase))
        {
            audioPeak = ParseDouble(match.Groups["peak"].Value);
        }

        return (audioPeak, BlackFramePattern.Matches(result.StandardError).Count);
    }

    private async Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string operation,
        CancellationToken cancellationToken,
        bool acceptExitCodeOne = false)
    {
        var stopwatch = Stopwatch.StartNew();
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start())
            {
                throw new ReelRenderException($"{operation} could not start '{executable}'.");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new ReelRenderException(
                $"{operation} could not start '{executable}'. Install FFmpeg or configure its executable path.", ex);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.ProcessTimeout);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        stopwatch.Stop();

        if (process.ExitCode != 0 && !(acceptExitCodeOne && process.ExitCode == 1))
        {
            throw new ReelRenderException(
                $"{operation} failed with exit code {process.ExitCode}: {Tail(stderr, 4_000)}");
        }

        logger.LogDebug(
            "{Operation} completed with exit code {ExitCode} in {Duration} ms.",
            operation,
            process.ExitCode,
            stopwatch.ElapsedMilliseconds);
        return new ProcessResult(stdout, stderr);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between HasExited and Kill.
        }
    }

    private static string Tail(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[^maxLength..];

    private static double ParseFrameRate(string? value)
    {
        var parts = value?.Split('/') ?? [];

        if (parts.Length == 2)
        {
            var numerator = ParseDouble(parts[0]);
            var denominator = ParseDouble(parts[1]);
            return denominator == 0 ? 0 : numerator / denominator;
        }

        return ParseDouble(value);
    }

    private static double ParseDouble(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private sealed record ProcessResult(string StandardOutput, string StandardError);
}

public sealed record SceneLayerFiles(string BackgroundPath, string ProductPath, string TextPath);
