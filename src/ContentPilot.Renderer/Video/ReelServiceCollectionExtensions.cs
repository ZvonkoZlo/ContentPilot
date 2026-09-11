using ContentPilot.Rendering.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace ContentPilot.Renderer.Video;

public static class ReelServiceCollectionExtensions
{
    public static IServiceCollection AddContentPilotReels(
        this IServiceCollection services,
        Action<ReelRendererOptions>? configure = null)
    {
        var options = new ReelRendererOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.AddSingleton<ReelTemplateCatalog>();
        services.AddSingleton<ReelDocumentBuilder>();
        services.AddSingleton<ReelLayerRenderer>();
        services.AddSingleton<FfmpegRunner>();
        services.AddSingleton<SceneComposer>();
        services.AddSingleton<ReelKeyframeExtractor>();
        services.AddSingleton<ReelQaService>();
        services.AddSingleton<FfmpegReelRenderer>();
        services.AddSingleton<IReelRenderer>(provider => provider.GetRequiredService<FfmpegReelRenderer>());

        return services;
    }
}
