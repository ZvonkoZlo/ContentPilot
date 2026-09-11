using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContentPilot.Application.Ai;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ContentPilot.Infrastructure.Ai;

/// <summary>
/// Records real responses to disk and replays them.
/// <para>
/// This is what makes the agent tests meaningful and runnable at once: they exercise the
/// real prompts and the real validators against real model output, with no API key, no
/// spend and no flakiness. A test that mocks the model only proves the mock works.
/// </para>
/// <para>
/// In Replay a missing cassette is an error. Falling through to a live call would turn a
/// CI run into a bill, and would make a green suite depend on the network.
/// </para>
/// </summary>
public sealed class CassetteLanguageModelClient(
    ILanguageModelClient inner,
    IOptions<AiOptions> options,
    ILogger<CassetteLanguageModelClient> logger) : ILanguageModelClient
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly AiOptions _options = options.Value;

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        if (_options.Cassettes == CassetteMode.Off)
        {
            return await inner.CompleteAsync(request, ct);
        }

        var path = PathFor(request);

        if (_options.Cassettes == CassetteMode.Replay)
        {
            if (!File.Exists(path))
            {
                throw new InvalidOperationException(
                    $"No cassette for '{request.Operation}' at {path}. Re-record with " +
                    "Ai:Cassettes=Record and an API key; replay never calls the provider.");
            }

            var recorded = JsonSerializer.Deserialize<Cassette>(
                await File.ReadAllTextAsync(path, ct), Json)!;

            logger.LogDebug("Replayed {Operation} from cassette.", request.Operation);

            return new LlmResponse
            {
                Json = recorded.Json,
                ModelId = recorded.ModelId,
                Usage = new TokenUsage(recorded.InputTokens, recorded.OutputTokens, recorded.CacheReadTokens, 0),
                DurationMs = recorded.DurationMs,
                CostMicroCents = recorded.CostMicroCents,
                RefusalCategory = recorded.RefusalCategory,
                FromCassette = true,
            };
        }

        var response = await inner.CompleteAsync(request, ct);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new Cassette
        {
            Operation = request.Operation,
            Profile = request.Profile,
            ModelId = response.ModelId,
            // Kept so a human can read what produced this, and so a prompt change is
            // visible in the diff rather than only in a changed hash.
            SystemPrompt = request.System,
            UserPrompt = request.User,
            Json = response.Json,
            InputTokens = response.Usage.InputTokens,
            OutputTokens = response.Usage.OutputTokens,
            CacheReadTokens = response.Usage.CacheReadTokens,
            CostMicroCents = response.CostMicroCents,
            DurationMs = response.DurationMs,
            RefusalCategory = response.RefusalCategory,
            RecordedAt = DateTimeOffset.UtcNow,
        }, Json), ct);

        logger.LogInformation("Recorded cassette for {Operation} at {Path}.", request.Operation, path);

        return response;
    }

    /// <summary>
    /// Keyed by the prompt content, not by call order. Order-keyed cassettes go stale the
    /// moment a test is reordered, and a changed prompt must miss rather than replay an
    /// answer to a question nobody asked any more.
    /// </summary>
    private string PathFor(LlmRequest request)
    {
        var material = $"{request.Profile}\n{request.System}\n{request.User}\n{request.ResponseSchema}";
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..16];
        var safeOperation = string.Concat(request.Operation.Select(c =>
            char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-'));

        return Path.Combine(_options.CassetteDirectory, $"{safeOperation}.{hash}.json");
    }

    private sealed record Cassette
    {
        public string Operation { get; init; } = string.Empty;
        public string Profile { get; init; } = string.Empty;
        public string ModelId { get; init; } = string.Empty;
        public string SystemPrompt { get; init; } = string.Empty;
        public string UserPrompt { get; init; } = string.Empty;
        public string Json { get; init; } = string.Empty;
        public long InputTokens { get; init; }
        public long OutputTokens { get; init; }
        public long CacheReadTokens { get; init; }
        public long CostMicroCents { get; init; }
        public int DurationMs { get; init; }
        public string? RefusalCategory { get; init; }
        public DateTimeOffset RecordedAt { get; init; }
    }
}
