using ContentPilot.Rendering.Contracts;

namespace ContentPilot.Renderer.Video;

public static class ReelSpecValidator
{
    private static readonly HashSet<string> AudioMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "audio/aac",
        "audio/m4a",
        "audio/mp4",
        "audio/mpeg",
        "audio/ogg",
        "audio/wav",
        "audio/x-wav",
    };

    public static void Validate(ReelSpec spec, ReelTemplateManifest manifest, int maxAssetBytes)
    {
        if (spec.TemplateVersion is { } pinned && pinned != manifest.Version)
        {
            throw new ReelRenderException(
                $"Reel template '{manifest.TemplateId}' is at version {manifest.Version}; the request pinned {pinned}.");
        }

        if (!manifest.ColorSchemes.Contains(spec.ColorScheme))
        {
            throw new ReelRenderException(
                $"Reel template '{manifest.TemplateId}' does not offer the {spec.ColorScheme} scheme.");
        }

        if (spec.Options.FramesPerSecond != 30)
        {
            throw new ReelRenderException("MVP reels render at exactly 30 fps.");
        }

        if (spec.Options.ConstantRateFactor is < 0 or > 51 || spec.Options.MusicVolume is < 0 or > 1)
        {
            throw new ReelRenderException("CRF must be between 0 and 51 and music volume between 0 and 1.");
        }

        if (spec.DurationSeconds <= 0 || spec.Options.DurationToleranceSeconds is < 0 or > 1)
        {
            throw new ReelRenderException("Reel duration and duration tolerance must be positive and bounded.");
        }

        if (spec.Scenes.Count != manifest.Scenes.Count)
        {
            throw new ReelRenderException(
                $"Reel template '{manifest.TemplateId}' requires {manifest.Scenes.Count} scenes in manifest order.");
        }

        for (var index = 0; index < manifest.Scenes.Count; index++)
        {
            var requested = spec.Scenes[index];
            var declared = manifest.Scenes[index];

            if (!string.Equals(requested.SceneId, declared.Id, StringComparison.Ordinal))
            {
                throw new ReelRenderException(
                    $"Scene {index + 1} must be '{declared.Id}', not '{requested.SceneId}'.");
            }

            if (requested.DurationSeconds < declared.MinDurationSeconds ||
                requested.DurationSeconds > declared.MaxDurationSeconds)
            {
                throw new ReelRenderException(
                    $"Scene '{declared.Id}' duration must be between {declared.MinDurationSeconds:0.###} and " +
                    $"{declared.MaxDurationSeconds:0.###} seconds.");
            }

            foreach (var slot in declared.TextSlots.Where(s => s.Required))
            {
                if (!requested.Text.TryGetValue(slot.Id, out var value) || string.IsNullOrWhiteSpace(value))
                {
                    throw new ReelRenderException($"Scene '{declared.Id}' required text slot '{slot.Id}' is empty.");
                }
            }

            foreach (var (slotId, value) in requested.Text)
            {
                var slot = declared.FindTextSlot(slotId)
                    ?? throw new ReelRenderException($"Scene '{declared.Id}' has no text slot '{slotId}'.");
                var budget = slot.BudgetFor(spec.Language);

                if (value.Length > budget)
                {
                    throw new ReelRenderException(
                        $"Scene '{declared.Id}' slot '{slotId}' carries {value.Length} characters; " +
                        $"the budget for '{spec.Language}' is {budget}.");
                }
            }

            foreach (var slot in declared.AssetSlots.Where(s => s.Required))
            {
                if (!requested.Assets.ContainsKey(slot.Id))
                {
                    throw new ReelRenderException($"Scene '{declared.Id}' required asset slot '{slot.Id}' is empty.");
                }
            }

            foreach (var (slotId, payload) in requested.Assets)
            {
                if (declared.FindAssetSlot(slotId) is null)
                {
                    throw new ReelRenderException($"Scene '{declared.Id}' has no asset slot '{slotId}'.");
                }

                ValidateBase64Size(payload.Base64, maxAssetBytes, $"Scene '{declared.Id}' asset '{slotId}'");
            }
        }

        var timelineDuration = Timeline.Duration(spec.Scenes, manifest.Scenes);

        if (Math.Abs(timelineDuration - spec.DurationSeconds) > 0.01)
        {
            throw new ReelRenderException(
                $"Scene durations and transition overlaps produce {timelineDuration:0.###} seconds, " +
                $"but the reel declares {spec.DurationSeconds:0.###} seconds.");
        }

        if (spec.Music is not null)
        {
            if (!AudioMediaTypes.Contains(spec.Music.MediaType))
            {
                throw new ReelRenderException($"Music media type '{spec.Music.MediaType}' is not supported.");
            }

            ValidateBase64Size(spec.Music.Base64, maxAssetBytes * 2, "Music bed");
        }
    }

    private static void ValidateBase64Size(string base64, int maxBytes, string name)
    {
        if (string.IsNullOrWhiteSpace(base64))
        {
            throw new ReelRenderException($"{name} is empty.");
        }

        var approximateBytes = base64.Length / 4 * 3;

        if (approximateBytes > maxBytes)
        {
            throw new ReelRenderException(
                $"{name} is roughly {approximateBytes / 1024 / 1024} MB; the ceiling is {maxBytes / 1024 / 1024} MB.");
        }

        try
        {
            _ = Convert.FromBase64String(base64);
        }
        catch (FormatException ex)
        {
            throw new ReelRenderException($"{name} is not valid base64.", ex);
        }
    }
}

public static class Timeline
{
    public static double Duration(
        IReadOnlyList<ReelSceneSpec> scenes,
        IReadOnlyList<ReelSceneManifest> manifests) =>
        scenes.Sum(s => s.DurationSeconds) - manifests.Take(manifests.Count - 1).Sum(s => s.TransitionSeconds);

    public static IReadOnlyList<double> KeyframeTimestamps(
        IReadOnlyList<ReelSceneSpec> scenes,
        IReadOnlyList<ReelSceneManifest> manifests)
    {
        var timestamps = new SortedSet<double> { 0 };
        var start = 0.0;

        for (var index = 0; index < scenes.Count; index++)
        {
            var scene = scenes[index];
            var declared = manifests[index];

            timestamps.Add(start + (scene.DurationSeconds / 2));

            if (index < scenes.Count - 1)
            {
                var transitionStart = start + scene.DurationSeconds - declared.TransitionSeconds;
                timestamps.Add(transitionStart);
                timestamps.Add(start + scene.DurationSeconds);
                start = transitionStart;
            }
        }

        var duration = Duration(scenes, manifests);
        return timestamps.Where(t => IsValid(t, duration)).ToArray();
    }

    private static bool IsValid(double timestamp, double duration) =>
        timestamp >= 0 && timestamp < duration - (1.0 / 60.0);
}

public sealed class ReelRenderException : Exception
{
    public ReelRenderException(string message)
        : base(message)
    {
    }

    public ReelRenderException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
