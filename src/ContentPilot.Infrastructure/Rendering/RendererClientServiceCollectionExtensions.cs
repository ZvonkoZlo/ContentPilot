using ContentPilot.Application.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ContentPilot.Infrastructure.Rendering;

public static class RendererClientServiceCollectionExtensions
{
    public static IServiceCollection AddContentPilotRendererClient(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RendererClientOptions>()
            .Bind(configuration.GetSection(RendererClientOptions.SectionName))
            .Validate(o => Uri.TryCreate(o.BaseUrl, UriKind.Absolute, out _), "Renderer:BaseUrl must be an absolute URL.")
            .ValidateOnStart();

        services.AddHttpClient<IRendererClient, HttpRendererClient>((sp, http) =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<RendererClientOptions>>().Value;

            http.BaseAddress = new Uri(options.BaseUrl);
            http.Timeout = options.Timeout;
        });

        return services;
    }
}
