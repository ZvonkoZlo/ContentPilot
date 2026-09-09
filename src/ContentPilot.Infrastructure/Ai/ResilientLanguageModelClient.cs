using ContentPilot.Application.Ai;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ContentPilot.Infrastructure.Ai;

/// <summary>
/// Retries transport failures, and nothing else.
/// <para>
/// The distinction is the point. A 429 or a 503 is the provider having a moment and is
/// worth retrying with backoff. A schema rejection is the prompt being wrong, and retrying
/// it costs money to fail the same way — that one belongs to the agent's own quality
/// budget, which is why it is counted separately on <c>ContentItem</c>.
/// </para>
/// </summary>
public sealed class ResilientLanguageModelClient(
    ILanguageModelClient inner,
    IOptions<AiOptions> options,
    ILogger<ResilientLanguageModelClient> logger) : ILanguageModelClient
{
    private readonly AiOptions _options = options.Value;

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        var attempt = 0;

        while (true)
        {
            attempt++;

            try
            {
                return await inner.CompleteAsync(request, ct);
            }
            catch (Exception ex) when (IsTransient(ex) && attempt <= _options.MaxTransientRetries)
            {
                // Exponential with jitter. Without the jitter, several items failing at the
                // same moment retry in lockstep and rebuild the spike that caused it.
                var backoff = TimeSpan.FromMilliseconds(
                    Math.Pow(2, attempt) * 500 + Random.Shared.Next(0, 500));

                logger.LogWarning(ex,
                    "{Operation} failed transiently (attempt {Attempt}/{Max}); retrying in {Delay}ms.",
                    request.Operation, attempt, _options.MaxTransientRetries, (int)backoff.TotalMilliseconds);

                await Task.Delay(backoff, ct);
            }
        }
    }

    /// <summary>
    /// Matched on type and message rather than on a provider exception hierarchy, so the
    /// classification survives an SDK upgrade that renames its exception classes.
    /// </summary>
    private static bool IsTransient(Exception ex)
    {
        if (ex is LlmSchemaException or LlmBudgetExceededException or OperationCanceledException)
        {
            return false;
        }

        if (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return true;
        }

        var text = ex.Message;

        return text.Contains("429", StringComparison.Ordinal)
            || text.Contains("500", StringComparison.Ordinal)
            || text.Contains("502", StringComparison.Ordinal)
            || text.Contains("503", StringComparison.Ordinal)
            || text.Contains("529", StringComparison.Ordinal)
            || text.Contains("overloaded", StringComparison.OrdinalIgnoreCase)
            || text.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
            || text.Contains("timed out", StringComparison.OrdinalIgnoreCase);
    }
}
