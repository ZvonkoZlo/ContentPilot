using System.Diagnostics;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using ContentPilot.Application.Ai;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ContentPilot.Infrastructure.Ai;

/// <summary>
/// The one place this system talks to Anthropic.
/// <para>
/// Everything else in the AI layer decorates this: cost accounting, budget enforcement,
/// retries and cassettes are separate wrappers, so none of that logic is entangled with
/// the provider's request shape and each piece is testable on its own.
/// </para>
/// </summary>
public sealed class AnthropicLanguageModelClient : ILanguageModelClient
{
    private readonly AnthropicClient _client;
    private readonly IModelProfileRegistry _profiles;
    private readonly AiOptions _options;
    private readonly ILogger<AnthropicLanguageModelClient> _logger;

    public AnthropicLanguageModelClient(
        IModelProfileRegistry profiles,
        IOptions<AiOptions> options,
        ILogger<AnthropicLanguageModelClient> logger)
    {
        _profiles = profiles;
        _options = options.Value;
        _logger = logger;

        if (!_options.Enabled)
        {
            // Constructed but inert. The guard lives in CompleteAsync so a disabled layer
            // can still be wired up and inspected.
            _client = null!;
            return;
        }

        var apiKey = _options.ApiKey ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");

        _client = string.IsNullOrWhiteSpace(apiKey)
            ? new AnthropicClient()
            : new AnthropicClient { ApiKey = apiKey };
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
        var started = Stopwatch.GetTimestamp();

        var parameters = new MessageCreateParams
        {
            Model = profile.ModelId,
            MaxTokens = request.MaxOutputTokens ?? profile.MaxOutputTokens,

            // Adaptive thinking with an explicit effort level. Depth is a property of the
            // task, so it belongs to the profile rather than to each call site.
            Thinking = new ThinkingConfigAdaptive(),
            OutputConfig = new OutputConfig
            {
                Effort = ToEffort(profile.Effort),

                // The provider enforces the schema. Without this every agent would begin
                // with a parser that tolerates the model's mood.
                Format = new JsonOutputFormat { Schema = ParseSchema(request.ResponseSchema) },
            },

            // The system half carries the instructions and the brand block and repeats
            // across a whole campaign, so it is the half worth caching. The user half
            // differs every call and would only invalidate the prefix.
            System = request.CacheSystemPrompt
                ? new List<TextBlockParam>
                {
                    new() { Text = request.System, CacheControl = new CacheControlEphemeral() },
                }
                : request.System,

            Messages = [new() { Role = Role.User, Content = request.User }],
        };

        var response = await _client.Messages.Create(parameters, cancellationToken: ct);
        var durationMs = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        var usage = new TokenUsage(
            response.Usage.InputTokens,
            response.Usage.OutputTokens,
            response.Usage.CacheReadInputTokens ?? 0,
            response.Usage.CacheCreationInputTokens ?? 0);

        // A refusal is an outcome with a category, not an exception. The caller decides
        // whether to rephrase, fall back, or hand the item to a person.
        if (response.StopReason == "refusal")
        {
            var category = response.StopDetails?.Category ?? "unspecified";

            _logger.LogWarning(
                "{Operation} was refused by {Model} ({Category}).", request.Operation, profile.ModelId, category);

            return new LlmResponse
            {
                Json = "{}",
                ModelId = profile.ModelId,
                Usage = usage,
                DurationMs = durationMs,
                CostMicroCents = profile.PriceOf(usage),
                RefusalCategory = category,
            };
        }

        var json = string.Concat(response.Content
            .Select(block => block.Value)
            .OfType<TextBlock>()
            .Select(block => block.Text));

        if (string.IsNullOrWhiteSpace(json))
        {
            throw new LlmSchemaException(
                $"{request.Operation} returned no text content (stop reason: {response.StopReason}).");
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

    private static Dictionary<string, JsonElement> ParseSchema(string schemaJson)
    {
        using var document = JsonDocument.Parse(schemaJson);

        return document.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone());
    }

    /// <summary>
    /// Anthropic SDK 12.46.0 exposes Low, Medium, High and Max — it has no XHigh, which sits
    /// between High and Max. Rather than silently sending something else, XHigh maps down to
    /// High: the conservative direction, since mapping up would spend more than the profile
    /// asked for. Remove this branch once the SDK carries the level.
    /// </summary>
    private static Effort ToEffort(ModelEffort effort) => effort switch
    {
        ModelEffort.Low => Effort.Low,
        ModelEffort.Medium => Effort.Medium,
        ModelEffort.High => Effort.High,
        ModelEffort.XHigh => Effort.High,
        ModelEffort.Max => Effort.Max,
        _ => Effort.High,
    };
}
