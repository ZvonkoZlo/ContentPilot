using ContentPilot.Rendering.Contracts;

namespace ContentPilot.Renderer.Video;

/// <summary>Runs deterministic checks only; no video or keyframe is sent to a vision model.</summary>
public sealed class ReelQaService
{
    public ReelRenderReport BuildReport(
        ReelSpec spec,
        ReelTemplateManifest manifest,
        IReadOnlyList<RenderedSceneLayers> rendered,
        IReadOnlyList<ReelKeyframe> keyframes,
        VideoProbe probe,
        long renderDurationMs)
    {
        var checks = new List<ReelCheckResult>();
        var expectedDuration = Timeline.Duration(spec.Scenes, manifest.Scenes);

        checks.Add(Check(
            ReelCheckCode.Duration,
            Math.Abs(probe.DurationSeconds - expectedDuration) <= spec.Options.DurationToleranceSeconds,
            $"Expected {expectedDuration:0.###} s ± {spec.Options.DurationToleranceSeconds:0.###}; measured {probe.DurationSeconds:0.###} s."));
        checks.Add(Check(
            ReelCheckCode.FrameRate,
            Math.Abs(probe.FramesPerSecond - spec.Options.FramesPerSecond) <= 0.01,
            $"Expected {spec.Options.FramesPerSecond} fps; measured {probe.FramesPerSecond:0.###} fps."));
        checks.Add(Check(
            ReelCheckCode.Resolution,
            probe.Width == 1080 && probe.Height == 1920,
            $"Expected 1080x1920; measured {probe.Width}x{probe.Height}."));
        checks.Add(Check(
            ReelCheckCode.AudioTrack,
            probe.HasAudio,
            probe.HasAudio ? "AAC audio track is present." : "No audio track is present."));

        var audioPeakPasses = !probe.HasAudio || probe.AudioPeakDbfs is null || probe.AudioPeakDbfs <= -1;
        checks.Add(Check(
            ReelCheckCode.AudioPeak,
            audioPeakPasses,
            probe.AudioPeakDbfs is { } peak
                ? $"Audio peaks at {peak:0.0} dBFS; ceiling is -1.0 dBFS."
                : "Audio is silent and cannot clip."));

        var blackKeyframes = keyframes.Count(frame => frame.BlackPixelFraction >= 0.98);
        checks.Add(Check(
            ReelCheckCode.BlackFrames,
            probe.BlackFrameCount == 0 && blackKeyframes == 0,
            $"FFmpeg found {probe.BlackFrameCount} black ranges; extracted keyframes found {blackKeyframes} black frames."));

        var tooShort = spec.Scenes
            .Where(scene => scene.Text.Count > 0 && scene.DurationSeconds - 0.78 < 1.2)
            .Select(scene => scene.SceneId)
            .ToArray();
        checks.Add(Check(
            ReelCheckCode.TextReadability,
            tooShort.Length == 0,
            tooShort.Length == 0
                ? "Every text layer is fully visible for at least 1.2 seconds."
                : $"Text is visible for less than 1.2 seconds in: {string.Join(", ", tooShort)}."));

        var layoutFailures = StaticLayoutFailures(spec, manifest, rendered);
        checks.Add(Check(
            ReelCheckCode.StaticLayout,
            layoutFailures.Count == 0,
            layoutFailures.Count == 0
                ? "Scene reports pass overflow, line, contrast, safe-area and immutable-asset checks."
                : string.Join(" ", layoutFailures)));

        return new ReelRenderReport
        {
            Width = probe.Width,
            Height = probe.Height,
            DurationSeconds = probe.DurationSeconds,
            FramesPerSecond = probe.FramesPerSecond,
            HasAudio = probe.HasAudio,
            AudioPeakDbfs = probe.AudioPeakDbfs,
            BlackFrameCount = probe.BlackFrameCount,
            RenderDurationMs = renderDurationMs,
            Scenes = BuildSceneReports(spec, manifest, rendered),
            Checks = checks,
        };
    }

    private static IReadOnlyList<string> StaticLayoutFailures(
        ReelSpec spec,
        ReelTemplateManifest manifest,
        IReadOnlyList<RenderedSceneLayers> rendered)
    {
        var failures = new List<string>();

        for (var index = 0; index < rendered.Count; index++)
        {
            var scene = rendered[index];
            var declared = manifest.Scenes[index];
            var textSlots = declared.TextSlots.ToDictionary(slot => slot.Id, StringComparer.Ordinal);
            var assetSlots = declared.AssetSlots.ToDictionary(slot => slot.Id, StringComparer.Ordinal);

            if (scene.Report.BlockedRequests.Count > 0)
            {
                failures.Add($"{scene.SceneId} attempted external requests.");
            }

            if (!scene.Report.FontsLoaded.Contains(spec.Brand.HeadingFont, StringComparer.OrdinalIgnoreCase) ||
                !scene.Report.FontsLoaded.Contains(spec.Brand.BodyFont, StringComparer.OrdinalIgnoreCase))
            {
                failures.Add($"{scene.SceneId} did not resolve both brand fonts.");
            }

            foreach (var slot in scene.Report.Slots)
            {
                if (textSlots.TryGetValue(slot.SlotId, out var text))
                {
                    if (slot.Overflows || slot.LineCount > text.MaxLines)
                    {
                        failures.Add($"{scene.SceneId}/{slot.SlotId} overflows its declared box.");
                    }

                    if (slot.BreaksSafeArea)
                    {
                        failures.Add($"{scene.SceneId}/{slot.SlotId} enters the platform safe area.");
                    }

                    if (slot.ContrastRatio is { } ratio && ratio < 3)
                    {
                        failures.Add($"{scene.SceneId}/{slot.SlotId} has only {ratio:0.0}:1 contrast.");
                    }
                }

                if (assetSlots.TryGetValue(slot.SlotId, out var asset) &&
                    asset.Immutable && slot.Occlusion > asset.MaxOcclusion)
                {
                    failures.Add($"{scene.SceneId}/{slot.SlotId} is {slot.Occlusion:P1} occluded.");
                }
            }
        }

        return failures;
    }

    private static IReadOnlyList<ReelSceneRenderReport> BuildSceneReports(
        ReelSpec spec,
        ReelTemplateManifest manifest,
        IReadOnlyList<RenderedSceneLayers> rendered)
    {
        var reports = new List<ReelSceneRenderReport>(rendered.Count);
        var start = 0.0;

        for (var index = 0; index < rendered.Count; index++)
        {
            var end = start + spec.Scenes[index].DurationSeconds;
            reports.Add(new ReelSceneRenderReport
            {
                SceneId = rendered[index].SceneId,
                StartsAtSeconds = start,
                EndsAtSeconds = end,
                StaticReport = rendered[index].Report,
            });

            if (index < rendered.Count - 1)
            {
                start = end - manifest.Scenes[index].TransitionSeconds;
            }
        }

        return reports;
    }

    private static ReelCheckResult Check(ReelCheckCode code, bool passed, string detail) =>
        new() { Code = code, Passed = passed, Detail = detail };
}
