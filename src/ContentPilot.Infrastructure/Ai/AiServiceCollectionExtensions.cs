using ContentPilot.Application.Agents;
using ContentPilot.Application.Ai;
using ContentPilot.Application.Prompts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ContentPilot.Infrastructure.Ai;

/// <summary>
/// Wires the language model layer as a chain of decorators. The order is load-bearing and
/// reads outermost first:
/// <list type="number">
/// <item>budget — refuses before anything is spent, and records what was</item>
/// <item>cassette — replays without reaching the provider or the ledger</item>
/// <item>resilience — retries transport failures, never schema ones</item>
/// <item>routing — picks the adapter the profile names</item>
/// <item>the vendor adapter itself</item>
/// </list>
/// Budget sits outside cassettes so a replay costs nothing and never pollutes the ledger,
/// and outside retries so three attempts at one call cannot each pass a check the first
/// one already used up.
/// </summary>
public static class AiServiceCollectionExtensions
{
    public static IServiceCollection AddContentPilotAi(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AiOptions>()
            .Bind(configuration.GetSection(AiOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingleton<IModelProfileRegistry, ModelProfileRegistry>();
        services.TryAddSingleton<AnthropicLanguageModelClient>();
        services.TryAddSingleton<OpenAiLanguageModelClient>();
        services.TryAddSingleton<ProviderRoutingLanguageModelClient>();

        services.TryAddScoped<ILanguageModelClient>(provider =>
        {
            ILanguageModelClient client = provider.GetRequiredService<ProviderRoutingLanguageModelClient>();

            client = ActivatorUtilities.CreateInstance<ResilientLanguageModelClient>(provider, client);
            client = ActivatorUtilities.CreateInstance<CassetteLanguageModelClient>(provider, client);
            client = ActivatorUtilities.CreateInstance<BudgetedLanguageModelClient>(provider, client);

            return client;
        });

        // The library is immutable once loaded, so one instance serves the process.
        services.TryAddSingleton(_ => PromptLibrary.LoadEmbedded());
        services.TryAddScoped<PromptRegistry>();
        services.TryAddScoped<AgentExecutor>();

        services.TryAddSingleton<ContentStrategistAgent>();
        services.TryAddSingleton<CopywriterAgent>();
        services.TryAddSingleton<VisualQaAgent>();
        services.TryAddSingleton<MarketingQaAgent>();
        services.TryAddScoped<Branding.ContentMemoryReader>();

        return services;
    }
}
