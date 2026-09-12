using System.Text.Json;
using ContentPilot.Application.Agents;
using ContentPilot.Application.ContentMemory;
using ContentPilot.Domain.Content;
using ContentPilot.Domain.Workflow;
using ContentPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ContentPilot.Infrastructure.Content;

/// <summary>
/// §14: writes the <see cref="ContentHistoryEntry"/> an approved item leaves behind — the
/// same shape whether the item was approved on a clean first pass
/// (<c>ContentItemWorkflowJobHandler</c>) or after a human resolved its
/// <see cref="ContentItemStatus.NeedsHumanReview"/> state (the campaign endpoints' approve
/// action). Never called for anything else: content that failed QA should not block a
/// future topic.
/// </summary>
public sealed class ContentHistoryRecorder(AppDbContext db)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <param name="qualityScore">
    /// The caller's to compute, deliberately — a caller mid-transaction with QA reports
    /// only <c>Add()</c>-ed, not yet saved, cannot get this from a fresh query (an
    /// <c>AsNoTracking</c> read always hits the database, never the local change tracker),
    /// so it has to come from whatever the caller already holds in memory.
    /// </param>
    public async Task RecordAsync(
        ContentItem item, Guid brandId, Guid workflowRunId, int attempt, double? qualityScore, DateTimeOffset now, CancellationToken ct)
    {
        var copySet = await LoadCopySetAsync(workflowRunId, attempt, ct);
        var hook = copySet?.Slots.FirstOrDefault()?.Text ?? item.Topic;
        var templateId = await LoadTemplateIdAsync(workflowRunId, ct);

        var entry = new ContentHistoryEntry(
            item.TenantId, brandId, item.Id, item.Type, item.Topic, item.Pillar, hook,
            SimHash.Compute(item.Topic), SimHash.Compute(hook), templateId, now);

        if (qualityScore is { } score)
        {
            entry.SetQualityScore(score);
        }

        db.ContentHistory.Add(entry);
    }

    /// <summary>
    /// The most recent successful Writing step at or before <paramref name="attempt"/>, not
    /// an exact match — Writing is only re-run when remediation specifically restarts
    /// there, so an item approved after restarting at a later step has no Writing step at
    /// its own final attempt number and must reuse an earlier one's copy.
    /// </summary>
    private async Task<CopySet?> LoadCopySetAsync(Guid workflowRunId, int attempt, CancellationToken ct)
    {
        var step = await db.WorkflowSteps.AsNoTracking()
            .Where(s => s.WorkflowRunId == workflowRunId && s.StepName == nameof(ContentItemStatus.Writing) &&
                        s.Attempt <= attempt && s.Outcome == WorkflowStepOutcome.Succeeded)
            .OrderByDescending(s => s.Attempt)
            .FirstOrDefaultAsync(ct);

        return step?.ResultJson is null ? null : JsonSerializer.Deserialize<CopySet>(step.ResultJson, Json);
    }

    /// <summary>
    /// Reads only the one field this needs (<c>templateId</c>) from Directing's result JSON
    /// via <see cref="JsonDocument"/> rather than depending on
    /// <c>ContentItemWorkflowJobHandler</c>'s private <c>DirectingResult</c> record.
    /// </summary>
    private async Task<string?> LoadTemplateIdAsync(Guid workflowRunId, CancellationToken ct)
    {
        var step = await db.WorkflowSteps.AsNoTracking()
            .Where(s => s.WorkflowRunId == workflowRunId && s.StepName == nameof(ContentItemStatus.Directing) &&
                        s.Outcome == WorkflowStepOutcome.Succeeded)
            .OrderByDescending(s => s.Attempt)
            .FirstOrDefaultAsync(ct);

        if (step?.ResultJson is null)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(step.ResultJson);
        return doc.RootElement.TryGetProperty("templateId", out var value) ? value.GetString() : null;
    }
}
