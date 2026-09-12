using ContentPilot.Application.Ai;
using ContentPilot.Domain.Observability;
using ContentPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ContentPilot.Infrastructure.Ai;

/// <summary>
/// Refuses a call that would take a campaign past its budget, and records what every call
/// cost.
/// <para>
/// The check is before the call, not after. A budget enforced after the fact is a report,
/// not a limit — an autonomous pipeline that retries can spend a great deal between two
/// human glances.
/// </para>
/// </summary>
public sealed class BudgetedLanguageModelClient(
    ILanguageModelClient inner,
    AppDbContext db,
    IModelProfileRegistry profiles,
    IOptions<AiOptions> options,
    ILogger<BudgetedLanguageModelClient> logger) : ILanguageModelClient
{
    private readonly AiOptions _options = options.Value;

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        // Diagnostics and one-off calls carry no campaign, so there is nothing to bill
        // against and nothing to enforce.
        if (request.CampaignId is not { } campaignId)
        {
            return await inner.CompleteAsync(request, ct);
        }

        var spent = await db.CostEntries
            .Where(entry => entry.CampaignId == campaignId)
            .SumAsync(entry => (long?)entry.AmountMicroCents, ct) ?? 0;

        var budget = _options.CampaignBudgetMicroCents;

        if (spent >= budget)
        {
            throw new LlmBudgetExceededException(
                $"Campaign {campaignId} has spent {spent / 100_000_000m:C} of its " +
                $"{budget / 100_000_000m:C} budget. Refusing {request.Operation}.");
        }

        var response = await inner.CompleteAsync(request, ct);

        // Cassette replays cost nothing and must not pollute the ledger, or a test run
        // would look like spend.
        if (!response.FromCassette)
        {
            RecordCost(request, response, campaignId);

            // Deliberately not saved here. AgentExecutor — the only real caller of this
            // client — is still mid-way through building the AgentRun for this same call
            // (it archives payloads and records the outcome only after CompleteAsync
            // returns) on this same shared, scoped DbContext. A save here would flush that
            // half-built, still-Added AgentRun to Unchanged early; AgentExecutor's later
            // mutations to it would then hit EF's append-only guard as a Modified entity,
            // exactly the exception this comment used to be missing. Every path through
            // AgentExecutor.RunAsync saves right after it finishes mutating the run, so the
            // CostEntry added here rides along on that save instead — cost entry and run
            // outcome commit atomically, which is strictly better than the split save this
            // replaced.
        }

        var after = spent + response.CostMicroCents;

        if (after > budget * 0.8)
        {
            logger.LogWarning(
                "Campaign {CampaignId} has used {Percent:P0} of its budget.", campaignId, (double)after / budget);
        }

        return response;
    }

    /// <summary>
    /// One row per token class. Splitting them is what later answers "would caching have
    /// helped?" — a single blended figure cannot.
    /// </summary>
    private void RecordCost(LlmRequest request, LlmResponse response, Guid campaignId)
    {
        var profile = profiles.Get(request.Profile);
        var tenantId = db.CurrentTenantId ?? throw new InvalidOperationException("No tenant in scope.");
        var now = DateTimeOffset.UtcNow;

        (CostKind Kind, long Units, long Rate)[] lines =
        [
            (CostKind.InputTokens, response.Usage.InputTokens, profile.InputPricePerMillion),
            (CostKind.OutputTokens, response.Usage.OutputTokens, profile.OutputPricePerMillion),
            (CostKind.CacheReadTokens, response.Usage.CacheReadTokens, profile.CacheReadPricePerMillion),
            (CostKind.CacheWriteTokens, response.Usage.CacheWriteTokens, profile.CacheWritePricePerMillion),
        ];

        foreach (var (kind, units, rate) in lines)
        {
            if (units <= 0)
            {
                continue;
            }

            db.CostEntries.Add(new CostEntry(
                tenantId, campaignId, request.ContentItemId, agentRunId: null, kind,
                provider: "anthropic", resource: response.ModelId, units, rate,
                amountMicroCents: (long)((decimal)units * rate / 1_000_000m), now));
        }
    }
}
