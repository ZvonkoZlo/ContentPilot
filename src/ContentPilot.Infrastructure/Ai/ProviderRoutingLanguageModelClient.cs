using ContentPilot.Application.Ai;

namespace ContentPilot.Infrastructure.Ai;

/// <summary>
/// Sends each request to the adapter its profile names.
/// <para>
/// Routing is per profile rather than per deployment, so one campaign can put the
/// strategist on one vendor and the copywriter on another — which is also the only honest
/// way to compare two models: same brief, same validators, same week.
/// </para>
/// <para>
/// This sits below the shared middleware. Budget, cassettes and retries behave identically
/// whichever vendor answers, because none of them knows which one did.
/// </para>
/// </summary>
public sealed class ProviderRoutingLanguageModelClient(
    IModelProfileRegistry profiles,
    AnthropicLanguageModelClient anthropic,
    OpenAiLanguageModelClient openAi) : ILanguageModelClient
{
    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        var profile = profiles.Get(request.Profile);

        ILanguageModelClient adapter = profile.Provider switch
        {
            ModelProvider.Anthropic => anthropic,
            ModelProvider.OpenAi => openAi,
            _ => throw new InvalidOperationException(
                $"Profile '{profile.Name}' names provider {profile.Provider}, which has no adapter."),
        };

        return adapter.CompleteAsync(request, ct);
    }
}
