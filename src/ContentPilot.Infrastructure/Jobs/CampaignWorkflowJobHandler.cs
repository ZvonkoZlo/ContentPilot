using ContentPilot.Application.Abstractions;
using ContentPilot.Application.Agents;
using ContentPilot.Application.Capabilities;
using ContentPilot.Application.Jobs;
using ContentPilot.Domain.Content;
using ContentPilot.Domain.Workflow;
using ContentPilot.Infrastructure.Ai;
using ContentPilot.Infrastructure.Branding;
using ContentPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ContentPilot.Infrastructure.Jobs;

/// <summary>
/// The campaign half of §6's caller: plans a campaign with the strategist, fans its items
/// out into their own <c>WorkflowRun</c>s, and — once every item has reached a terminal
/// state — closes the campaign out. Item-level self-correction is
/// <see cref="ContentItemWorkflowJobHandler"/>'s job entirely; this handler never touches a
/// <c>ContentItemStatus</c>, only <c>ContentCampaign.Status</c>, whose own transition
/// methods (<c>BeginPlanning</c>, <c>PlanAccepted</c>, and so on) already enforce §7's
/// campaign-level legality — the same guard <c>ItemStateMachine</c> gives items, already
/// built into the entity because a campaign's shape is far simpler than an item's.
/// <para>
/// <b>Scope of this pass.</b> No <c>WorkflowRun</c> is created at campaign scope — the
/// entity's own transition guard is enough for what this handler does, and nothing yet
/// needs a campaign-level attempt counter or deadline. "Packaging" is a single instantaneous
/// transition here, not the real ZIP-and-manifest step Phase 8 will add; ability to
/// re-plan a single repetitive item (§8's <c>Replan</c> outcome) does not exist, so those
/// items simply sit in <c>NeedsHumanReview</c> — see <c>ContentItemWorkflowJobHandler</c>'s
/// own notes.
/// </para>
/// </summary>
public sealed class CampaignWorkflowJobHandler(
    AppDbContext db,
    IJobQueue jobs,
    IClock clock,
    IBrandBrainReader brandReader,
    ContentMemoryReader recentContentReader,
    AgentExecutor agentExecutor,
    ContentStrategistAgent strategist,
    ILogger<CampaignWorkflowJobHandler> logger)
    : JobHandler<AdvanceCampaignWorkflowPayload>
{
    /// <summary>How long to wait before checking again whether every item has finished.</summary>
    private static readonly TimeSpan CompletionPollInterval = TimeSpan.FromMinutes(1);

    protected override async Task HandleAsync(
        JobExecutionContext context, AdvanceCampaignWorkflowPayload payload, CancellationToken ct)
    {
        var campaign = await db.ContentCampaigns.FirstOrDefaultAsync(c => c.Id == payload.CampaignId, ct);

        if (campaign is null)
        {
            throw new PermanentJobFailureException($"No campaign '{payload.CampaignId}'.");
        }

        if (campaign.IsTerminal)
        {
            return;
        }

        switch (campaign.Status)
        {
            case CampaignStatus.Draft:
            case CampaignStatus.Planning:
                await PlanAndFanOutAsync(campaign, context.TenantId, ct);
                break;

            case CampaignStatus.ItemsRunning:
                await CheckCompletionAsync(campaign, context.TenantId, ct);
                break;

            default:
                // Packaging is momentary in this pass (see the type's doc comment) and
                // transitions synchronously inside CheckCompletionAsync, so this handler
                // should never actually observe a campaign sitting in Packaging.
                logger.LogWarning("Campaign {CampaignId} is {Status}; this handler has nothing to do with it.", campaign.Id, campaign.Status);
                break;
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task PlanAndFanOutAsync(ContentCampaign campaign, Guid? tenantId, CancellationToken ct)
    {
        // Idempotency: a retry after a crash between "items created" and "jobs enqueued"
        // must not ask the strategist twice. The items already existing is the signal.
        var existingItems = await db.ContentItems
            .Where(i => i.CampaignId == campaign.Id)
            .OrderBy(i => i.Ordinal)
            .ToListAsync(ct);

        if (existingItems.Count == 0)
        {
            if (campaign.Status == CampaignStatus.Draft)
            {
                campaign.BeginPlanning();
            }

            var brand = await brandReader.GetVersionAsync(campaign.BrandProfileVersionId, ct);
            var recentContent = await recentContentReader.ReadAsync(campaign.BrandId, clock.UtcNow, ct);

            AgentResult<WeeklyPlan> result;

            try
            {
                result = await agentExecutor.RunAsync(
                    strategist,
                    new StrategistInput { Brand = brand, RecentContent = recentContent, WeekStart = campaign.WeekStart },
                    new AgentContext { CampaignId = campaign.Id },
                    ct);
            }
            catch (AgentValidationException ex)
            {
                campaign.Fail($"The strategist could not produce a valid plan: {ex.Message}", clock.UtcNow);
                return;
            }

            campaign.PlanAccepted(result.Value.Theme);

            var ordinal = 0;

            foreach (var planned in result.Value.Items)
            {
                var item = new ContentItem(
                    campaign.TenantId, campaign.Id,
                    Enum.Parse<ContentItemType>(planned.Type, ignoreCase: true),
                    planned.Topic, planned.Pillar, planned.Objective,
                    Enum.Parse<DayOfWeek>(planned.PublishDay, ignoreCase: true),
                    ++ordinal);

                db.ContentItems.Add(item);
                existingItems.Add(item);
            }

            // Item ids have to exist before a WorkflowRun can reference them.
            await db.SaveChangesAsync(ct);

            foreach (var item in existingItems)
            {
                db.WorkflowRuns.Add(new WorkflowRun(campaign.TenantId, campaign.Id, WorkflowScope.Item, item.Id, clock.UtcNow));
            }

            await db.SaveChangesAsync(ct);

            foreach (var item in existingItems)
            {
                await jobs.EnqueueAsync(new AdvanceContentItemWorkflowPayload(item.Id), tenantId, ct: ct);
            }
        }

        // Re-enqueue self to start polling for completion; every item job was just
        // enqueued (or already had been, on a retry) so there is nothing more to do now.
        await jobs.EnqueueAsync(
            new AdvanceCampaignWorkflowPayload(campaign.Id), tenantId, runAt: clock.UtcNow.Add(CompletionPollInterval), ct: ct);
    }

    private async Task CheckCompletionAsync(ContentCampaign campaign, Guid? tenantId, CancellationToken ct)
    {
        var items = await db.ContentItems
            .Where(i => i.CampaignId == campaign.Id)
            .Select(i => i.Status)
            .ToListAsync(ct);

        if (items.Count == 0 || items.Any(status => status is not (
            ContentItemStatus.Approved or ContentItemStatus.NeedsHumanReview or ContentItemStatus.Failed)))
        {
            await jobs.EnqueueAsync(
                new AdvanceCampaignWorkflowPayload(campaign.Id), tenantId, runAt: clock.UtcNow.Add(CompletionPollInterval), ct: ct);

            return;
        }

        var allApproved = items.All(status => status == ContentItemStatus.Approved);

        // A real packaging step (plan.json, manifest.json, the ZIP) is Phase 8's job; this
        // pass only needs the transition ContentCampaign's own state machine requires to
        // reach a terminal state honestly, rather than skipping a legal step.
        campaign.BeginPackaging();
        campaign.Complete(allApproved, clock.UtcNow);
    }
}
