using ContentPilot.Rendering.Contracts;
using ContentPilot.Renderer.Video;
using Shouldly;

namespace ContentPilot.RendererTests.Video;

public sealed class FfmpegFiltergraphBuilderTests
{
    private readonly ReelTemplateCatalog _catalog = ReelManifestTests.CreateCatalog();

    [Fact]
    public void Four_scenes_build_three_transitions_and_a_silent_audio_track()
    {
        var manifest = _catalog.Get("reel-feature-tour");
        var spec = ReelSamples.For(manifest);

        var plan = FfmpegFiltergraphBuilder.Build(spec, manifest);

        Count(plan.Filtergraph, "blend=all_expr").ShouldBe(2);
        Count(plan.Filtergraph, "overlay=x=0:y='H-H*t/").ShouldBe(1);
        plan.Filtergraph.ShouldContain("concat=n=7:v=1:a=0[timeline]");
        plan.Filtergraph.ShouldContain("[timeline]scale=1080:1920:flags=fast_bilinear");
        plan.Filtergraph.ShouldContain("anullsrc=channel_layout=stereo");
        plan.Filtergraph.ShouldContain("format=yuv420p");
        plan.VideoOutputLabel.ShouldBe("vout");
        plan.DurationSeconds.ShouldBe(15, tolerance: 0.002);
    }

    [Fact]
    public void Supplied_music_is_trimmed_ducked_and_faded()
    {
        var manifest = _catalog.Get("reel-before-after");
        var spec = ReelSamples.For(manifest) with
        {
            Music = BinaryPayload.FromBytes([1, 2, 3], "audio/mpeg"),
            Options = new ReelRenderOptions { MusicVolume = 0.2 },
        };

        var plan = FfmpegFiltergraphBuilder.Build(spec, manifest);

        plan.MusicInputIndex.ShouldBe(9);
        plan.Filtergraph.ShouldContain("[9:a]");
        plan.Filtergraph.ShouldContain("volume=0.2");
        plan.Filtergraph.ShouldContain("afade=t=in");
        plan.Filtergraph.ShouldNotContain("anullsrc");
    }

    [Fact]
    public void Keyframes_cover_scene_midpoints_and_both_transition_boundaries()
    {
        var manifest = _catalog.Get("reel-before-after");
        var spec = ReelSamples.For(manifest);

        var timestamps = Timeline.KeyframeTimestamps(spec.Scenes, manifest.Scenes);

        timestamps.ShouldContain(0);
        timestamps.ShouldContain(spec.Scenes[0].DurationSeconds / 2);
        timestamps.ShouldContain(spec.Scenes[0].DurationSeconds - manifest.Scenes[0].TransitionSeconds);
        timestamps.ShouldContain(spec.Scenes[0].DurationSeconds);
        timestamps.ShouldBe(timestamps.OrderBy(value => value).ToArray());
        timestamps.Distinct().Count().ShouldBe(timestamps.Count);
    }

    private static int Count(string value, string fragment) =>
        value.Split(fragment, StringSplitOptions.None).Length - 1;
}
