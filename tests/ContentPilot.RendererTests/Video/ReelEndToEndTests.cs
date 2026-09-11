using System.Text;
using ContentPilot.Rendering.Contracts;
using ImageMagick;
using Shouldly;

namespace ContentPilot.RendererTests.Video;

[Collection(ReelLayerCollection.Name)]
public sealed class ReelEndToEndTests(ReelLayerFixture fixture)
{
    private static readonly ulong[] ProblemSolutionGoldenHashes =
    [
        3071786058549742298,
        4503158080757928321,
        4503158080757928321,
        3289177878221694341,
        3289177878221694341,
        3289177878221694341,
        7660787437273748885,
        7660787437273748885,
        7660787437273748885,
        3694870121835611352,
        3694870121835611352,
    ];

    [FfmpegFact]
    public async Task Fifteen_second_reel_meets_the_container_keyframe_and_performance_contract()
    {
        var manifest = fixture.Catalog.Get("reel-problem-solution");
        var spec = ReelSamples.For(manifest) with
        {
            Music = BinaryPayload.FromBytes(CreateSineWave(), "audio/wav"),
        };

        var result = await fixture.FullRenderer.RenderAsync(spec, CancellationToken.None);

        result.Video.MediaType.ShouldBe("video/mp4");
        result.Video.ToBytes().Length.ShouldBeGreaterThan(100_000);
        result.Report.Width.ShouldBe(1080);
        result.Report.Height.ShouldBe(1920);
        result.Report.DurationSeconds.ShouldBe(15, tolerance: spec.Options.DurationToleranceSeconds);
        result.Report.FramesPerSecond.ShouldBe(30, tolerance: 0.01);
        result.Report.HasAudio.ShouldBeTrue();
        result.Report.BlackFrameCount.ShouldBe(0);
        result.Report.Passed.ShouldBeTrue(string.Join(" ", result.Report.Checks.Where(c => !c.Passed).Select(c => c.Detail)));
        result.Report.RenderDurationMs.ShouldBeLessThan(10_000, "A 15-second MVP reel must render in under ten seconds.");

        result.Keyframes.Count.ShouldBe(11);
        result.Keyframes[0].Kind.ShouldBe(ReelKeyframeKind.Cover);
        result.Keyframes.Select(frame => frame.TimestampSeconds)
            .ShouldBe(result.Keyframes.Select(frame => frame.TimestampSeconds).OrderBy(value => value));
        result.Keyframes.ShouldAllBe(frame => frame.BlackPixelFraction < 0.98);
        result.Keyframes.ShouldAllBe(frame => frame.PerceptualHash != 0);
        result.Keyframes.Select(frame => frame.PerceptualHash).ShouldBe(ProblemSolutionGoldenHashes);

        using var cover = new MagickImage(result.Cover.ToBytes());
        cover.Width.ShouldBe((uint)1080);
        cover.Height.ShouldBe((uint)1920);
    }

    private static byte[] CreateSineWave()
    {
        const int sampleRate = 48_000;
        const short channels = 1;
        const short bitsPerSample = 16;
        const double frequency = 220;
        var samples = sampleRate;
        var dataLength = samples * channels * (bitsPerSample / 8);

        using var stream = new MemoryStream(44 + dataLength);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataLength);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * (bitsPerSample / 8));
        writer.Write((short)(channels * (bitsPerSample / 8)));
        writer.Write(bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);

        for (var index = 0; index < samples; index++)
        {
            var sample = Math.Sin(2 * Math.PI * frequency * index / sampleRate) * 0.25;
            writer.Write((short)(sample * short.MaxValue));
        }

        writer.Flush();
        return stream.ToArray();
    }
}

public sealed class FfmpegFactAttribute : FactAttribute
{
    public FfmpegFactAttribute()
    {
        var ffmpeg = Environment.GetEnvironmentVariable("CONTENTPILOT_FFMPEG_PATH");
        var ffprobe = Environment.GetEnvironmentVariable("CONTENTPILOT_FFPROBE_PATH");

        if (string.IsNullOrWhiteSpace(ffmpeg) || string.IsNullOrWhiteSpace(ffprobe) ||
            !File.Exists(ffmpeg) || !File.Exists(ffprobe))
        {
            Skip = "Set CONTENTPILOT_FFMPEG_PATH and CONTENTPILOT_FFPROBE_PATH to run reel video integration tests.";
        }
    }
}
