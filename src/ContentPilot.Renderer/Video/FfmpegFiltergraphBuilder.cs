using System.Globalization;
using System.Text;
using ContentPilot.Rendering.Contracts;

namespace ContentPilot.Renderer.Video;

public sealed record FfmpegCompositionPlan(
    string Filtergraph,
    string VideoOutputLabel,
    string AudioOutputLabel,
    double DurationSeconds,
    int MusicInputIndex);

/// <summary>Builds the one-pass composition graph; process execution stays in <see cref="FfmpegRunner"/>.</summary>
public static class FfmpegFiltergraphBuilder
{
    public static FfmpegCompositionPlan Build(ReelSpec spec, ReelTemplateManifest manifest)
    {
        var fps = spec.Options.FramesPerSecond;
        var graph = new StringBuilder(4096);

        for (var index = 0; index < spec.Scenes.Count; index++)
        {
            var scene = spec.Scenes[index];
            var declared = manifest.Scenes[index];
            var duration = Num(scene.DurationSeconds);
            var baseInput = index * 3;

            graph.Append('[').Append(baseInput).Append(":v]")
                .Append("scale=522:918:force_original_aspect_ratio=increase,crop=522:918,")
                .Append("loop=loop=-1:size=1:start=0,setpts=N/(").Append(fps).Append("*TB),")
                .Append("fps=").Append(fps).Append(",trim=duration=").Append(duration).Append(',')
                .Append(BackgroundMotion(declared.BackgroundMotion, scene.DurationSeconds))
                .Append(",setpts=PTS-STARTPTS[bg").Append(index).Append("];\n");

            graph.Append('[').Append(baseInput + 1).Append(":v]")
                .Append("scale=486:864,loop=loop=-1:size=1:start=0,setpts=N/(").Append(fps).Append("*TB),")
                .Append("fps=").Append(fps).Append(",trim=duration=").Append(duration)
                .Append(",format=rgba,fade=t=in:st=0:d=0.35:alpha=1,")
                .Append("fade=t=out:st=").Append(Num(Math.Max(0, scene.DurationSeconds - 0.35)))
                .Append(":d=0.35:alpha=1,setpts=PTS-STARTPTS[product").Append(index).Append("];\n");

            graph.Append("[bg").Append(index).Append("][product").Append(index).Append(']')
                .Append("overlay=x=").Append(ProductX(declared.ProductMotion))
                .Append(":y=").Append(ProductY(declared.ProductMotion))
                .Append(":format=auto:eof_action=pass[visual").Append(index).Append("];\n");

            graph.Append('[').Append(baseInput + 2).Append(":v]")
                .Append("scale=486:864,loop=loop=-1:size=1:start=0,setpts=N/(").Append(fps).Append("*TB),")
                .Append("fps=").Append(fps).Append(",trim=duration=").Append(duration)
                .Append(",format=rgba,fade=t=in:st=0.12:d=0.38:alpha=1,")
                .Append("fade=t=out:st=").Append(Num(Math.Max(0, scene.DurationSeconds - 0.4)))
                .Append(":d=0.4:alpha=1,setpts=PTS-STARTPTS[text").Append(index).Append("];\n");

            graph.Append("[visual").Append(index).Append("][text").Append(index).Append(']')
                .Append("overlay=x=0:y='if(lt(t,0.5),24*(1-t/0.5),0)':format=auto:eof_action=pass,")
                .Append("format=yuv420p[scene").Append(index).Append("];\n");
        }

        var concatLabels = new List<string>((spec.Scenes.Count * 2) - 1);

        for (var index = 0; index < spec.Scenes.Count; index++)
        {
            var inputCount = 1 + (index > 0 ? 1 : 0) + (index < spec.Scenes.Count - 1 ? 1 : 0);
            var stableSource = $"scene{index}stableSource";
            var incomingSource = $"scene{index}incomingSource";
            var outgoingSource = $"scene{index}outgoingSource";

            graph.Append("[scene").Append(index).Append("]split=").Append(inputCount)
                .Append('[').Append(stableSource).Append(']');

            if (index > 0)
            {
                graph.Append('[').Append(incomingSource).Append(']');
            }

            if (index < spec.Scenes.Count - 1)
            {
                graph.Append('[').Append(outgoingSource).Append(']');
            }

            graph.Append(";\n");

            var stableStart = index == 0 ? 0 : manifest.Scenes[index - 1].TransitionSeconds;
            var stableEnd = spec.Scenes[index].DurationSeconds - manifest.Scenes[index].TransitionSeconds;
            var stableLabel = $"stable{index}";

            graph.Append('[').Append(stableSource).Append("]trim=start=").Append(Num(stableStart))
                .Append(":end=").Append(Num(stableEnd))
                .Append(",setpts=PTS-STARTPTS[").Append(stableLabel).Append("];\n");
            concatLabels.Add(stableLabel);

            if (index >= spec.Scenes.Count - 1)
            {
                continue;
            }

            var transition = manifest.Scenes[index];
            var overlap = transition.TransitionSeconds;
            var nextIncoming = $"scene{index + 1}incomingSource";
            var outgoingLabel = $"transition{index}outgoing";
            var incomingLabel = $"transition{index}incoming";
            var transitionLabel = $"transition{index}";

            graph.Append('[').Append(outgoingSource).Append("]trim=start=")
                .Append(Num(spec.Scenes[index].DurationSeconds - overlap))
                .Append(":end=").Append(Num(spec.Scenes[index].DurationSeconds))
                .Append(",setpts=PTS-STARTPTS[").Append(outgoingLabel).Append("];\n")
                .Append('[').Append(nextIncoming).Append("]trim=start=0:end=").Append(Num(overlap))
                .Append(",setpts=PTS-STARTPTS[").Append(incomingLabel).Append("];\n")
                .Append('[').Append(outgoingLabel).Append("][").Append(incomingLabel).Append(']')
                .Append(TransitionFilter(transition.Transition, overlap))
                .Append('[').Append(transitionLabel).Append("];\n");
            concatLabels.Add(transitionLabel);
        }

        foreach (var label in concatLabels)
        {
            graph.Append('[').Append(label).Append(']');
        }

        graph.Append("concat=n=").Append(concatLabels.Count)
            .Append(":v=1:a=0[timeline];\n[timeline]scale=1080:1920:flags=fast_bilinear,format=yuv420p[vout];\n");
        var currentDuration = Timeline.Duration(spec.Scenes, manifest.Scenes);

        var musicInputIndex = spec.Scenes.Count * 3;

        if (spec.Music is null)
        {
            graph.Append("anullsrc=channel_layout=stereo:sample_rate=48000,")
                .Append("atrim=duration=").Append(Num(currentDuration))
                .Append(",asetpts=N/SR/TB[aout]");
        }
        else
        {
            graph.Append('[').Append(musicInputIndex).Append(":a]")
                .Append("atrim=duration=").Append(Num(currentDuration))
                .Append(",asetpts=PTS-STARTPTS,volume=").Append(Num(spec.Options.MusicVolume))
                .Append(",afade=t=in:st=0:d=0.5,afade=t=out:st=")
                .Append(Num(Math.Max(0, currentDuration - 0.75)))
                .Append(":d=0.75[aout]");
        }

        return new FfmpegCompositionPlan(graph.ToString(), "vout", "aout", currentDuration, musicInputIndex);
    }

    private static string BackgroundMotion(ReelBackgroundMotion motion, double duration) => motion switch
    {
        ReelBackgroundMotion.PushIn =>
            $"crop=486:864:x='18+18*t/{Num(duration)}':y='27+27*t/{Num(duration)}'",
        ReelBackgroundMotion.PullOut =>
            $"crop=486:864:x='36-18*t/{Num(duration)}':y='54-27*t/{Num(duration)}'",
        _ => "crop=486:864:x=18:y=27",
    };

    private static string ProductX(ReelProductMotion motion) => motion switch
    {
        ReelProductMotion.SlideLeft => "'if(lt(t,0.65),W*0.08*(1-t/0.65),0)'",
        ReelProductMotion.SlideRight => "'if(lt(t,0.65),-W*0.08*(1-t/0.65),0)'",
        _ => "0",
    };

    private static string ProductY(ReelProductMotion motion) =>
        motion == ReelProductMotion.Rise ? "'if(lt(t,0.65),H*0.06*(1-t/0.65),0)'" : "0";

    private static string TransitionFilter(ReelTransition transition, double duration) => transition switch
    {
        ReelTransition.WipeLeft =>
            $"blend=all_expr='if(lt(X/W,T/{Num(duration)}),B,A)':shortest=1",
        ReelTransition.SlideUp =>
            $"overlay=x=0:y='H-H*t/{Num(duration)}':shortest=1:eof_action=pass",
        _ =>
            $"blend=all_expr='A*(1-T/{Num(duration)})+B*(T/{Num(duration)})':shortest=1",
    };

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
