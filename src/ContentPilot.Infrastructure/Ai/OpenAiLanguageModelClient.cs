using System.ClientModel;
using System.Diagnostics;
using System.Text.Json;
using ContentPilot.Application.Ai;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;

namespace ContentPilot.Infrastructure.Ai;

/// <summary>
/// The OpenAI adapter, on OpenAI's own SDK.
/// <para>
/// Deliberately a sibling of the Anthropic adapter rather than a shared "compatible" path.
/// Routing one vendor through another's compatibility shim quietly forfeits the features
/// these agents depend on — schema-enforced output and reasoning effort — and misreports
/// which model actually ran.
/// </para>
/// </summary>
public sealed class OpenAiLanguageModelClient : ILanguageModelClient
{
    private readonly OpenAIClient? _client;
    private readonly IModelProfileRegistry _profiles;
    private readonly AiOptions _options;
    private readonly ILogger<OpenAiLanguageModelClient> _logger;

    public OpenAiLanguageModelClient(
        IModelProfileRegistry profiles,
        IOptions<AiOptions> options,
        ILogger<OpenAiLanguageModelClient> logger)
    {
        _profiles = profiles;
        _options = options.Value;
        _logger = logger;

        if (!_options.Enabled)
        {
            return;
        }

        var configured = _options.Providers.GetValueOrDefault("OpenAi");
        var apiKey = configured?.ApiKey ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            // Left null rather than throwing: a deployment that only uses Anthropic profiles
            // should not be forced to hold an OpenAI key.
            return;
        }

        var clientOptions = new OpenAIClientOptions();

        if (!string.IsNullOrWhiteSpace(configured?.BaseUrl))
        {
            clientOptions.Endpoint = new Uri(configured.BaseUrl);
        }

        _client = new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions);
    }

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        if (!_options.Enabled)
        {
            throw new InvalidOperationException(
                "The language model layer is disabled (Ai:Enabled is false). Enable it, or " +
                "run with cassettes in Replay mode.");
        }

        var profile = _profiles.Get(request.Profile);

        if (profile.Provider != ModelProvider.OpenAi)
        {
            throw new InvalidOperationException(
                $"Profile '{profile.Name}' is configured for {profile.Provider} but reached the " +
                "OpenAI adapter. This is a routing bug, not a configuration error.");
        }

        if (_client is null)
        {
            throw new InvalidOperationException(
                $"Profile '{profile.Name}' needs an OpenAI key. Set Ai__Providers__OpenAi__ApiKey " +
                "(see .env.example), or point the profile at another provider.");
        }

        var chat = _client.GetChatClient(profile.ModelId);
        var started = Stopwatch.GetTimestamp();

        var completionOptions = new ChatCompletionOptions
        {
            // Strict, so the schema is genuinely enforced rather than merely suggested -
            // the same promise the Anthropic adapter makes. OpenAI's strict mode requires
            // every object to list all its properties in `required` and to set
            // `additionalProperties: false`; a schema that does not comply is rejected at
            // request time, which is the right place to find out.
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                jsonSchemaFormatName: SchemaNameFor(request.Operation),
                jsonSchema: BinaryData.FromString(request.ResponseSchema),
                jsonSchemaIsStrict: true),
        };

        var response = await chat.CompleteChatAsync(
            [new SystemChatMessage(request.System), new UserChatMessage(request.User)],
            completionOptions,
            ct);

        var durationMs = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var completion = response.Value;

        var usage = new TokenUsage(
            completion.Usage?.InputTokenCount ?? 0,
            completion.Usage?.OutputTokenCount ?? 0,
            completion.Usage?.InputTokenDetails?.CachedTokenCount ?? 0,
            // OpenAI caches automatically and does not bill a separate write, so there is
            // nothing to report here rather than a zero standing in for a missing number.
            0);

        if (completion.FinishReason == ChatFinishReason.ContentFilter)
        {
            _logger.LogWarning(
                "{Operation} was filtered by {Model}.", request.Operation, profile.ModelId);

            return new LlmResponse
            {
                Json = "{}",
                ModelId = profile.ModelId,
                Usage = usage,
                DurationMs = durationMs,
                CostMicroCents = profile.PriceOf(usage),
                RefusalCategory = "content_filter",
            };
        }

        var json = string.Concat(completion.Content.Select(part => part.Text));

        if (string.IsNullOrWhiteSpace(json))
        {
            throw new LlmSchemaException(
                $"{request.Operation} returned no content (finish reason: {completion.FinishReason}).");
        }

        _logger.LogInformation(
            "{Operation} on {Model}: {Input} in, {Output} out, {CacheRead} cached, {Duration}ms.",
            request.Operation, profile.ModelId, usage.InputTokens, usage.OutputTokens,
            usage.CacheReadTokens, durationMs);

        return new LlmResponse
        {
            Json = json,
            ModelId = profile.ModelId,
            Usage = usage,
            DurationMs = durationMs,
            CostMicroCents = profile.PriceOf(usage),
        };
    }

    /// <summary>
    /// OpenAI requires a name for the schema, and accepts only letters, digits, underscores
    /// and dashes.
    /// </summary>
    private static string SchemaNameFor(string operation) =>
        string.Concat(operation.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
}
