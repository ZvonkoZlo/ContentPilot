using System.Text.Json;
using ContentPilot.Application.Abstractions;
using ContentPilot.Application.Agents;
using ContentPilot.Application.Ai;
using ContentPilot.Application.Brand;
using ContentPilot.Application.Capabilities;
using ContentPilot.Application.Jobs;
using ContentPilot.Application.Orchestration;
using ContentPilot.Application.Quality;
using ContentPilot.Domain.Content;
using ContentPilot.Domain.Quality;
using ContentPilot.Domain.Workflow;
using ContentPilot.Infrastructure.Ai;
using ContentPilot.Infrastructure.Persistence;
using ContentPilot.Rendering.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ContentPilot.Infrastructure.Jobs;

/// <summary>
/// The caller §6 describes: leases nothing itself (the job queue already leased this job),
/// loads a <c>WorkflowRun</c>, calls <see cref="OrchestratorCore.Decide"/>, executes exactly
/// the action it names, and persists the result before deciding whether to re-enqueue.
/// <para>
/// One simplification, stated rather than hidden: every step is pure, idempotent-cheap, or
/// (Writing) memoised against redoing it, so a single job invocation drives an item through
/// as many of them as it can in one pass, calling <see cref="OrchestratorCore.Decide"/>
/// again after each — re-enqueuing only on a remediation restart (a fresh lease window
/// after spending a quality attempt) or when the iteration or step ceiling says to stop.
/// A crash mid-pass simply redoes the cheap steps on retry; only the copywriter's output is
/// memoised (in the Writing <c>WorkflowStep</c>'s <c>ResultJson</c>) so a resumed run does
/// not re-bill a model call that already succeeded.
/// </para>
/// <para>
/// <b>Scope of this pass.</b> Only <c>StaticPost</c> items are driven end to end — carousels
/// and reels need their own composition logic Phase 7/8 will add. Budget enforcement is not
/// wired in yet (no caller sums the ledger), so <see cref="WorkflowDecisionContext.Budget"/>
/// is always null here; §24's limits still apply at the counter level
/// (<c>WorkflowRun.QualityAttempts</c> etc.) but not yet at the cost level. Background image
/// generation does not exist, so <c>AssetGeneration</c> is a pass-through: only templates
/// whose required assets are all user uploads can complete.
/// </para>
/// </summary>
public sealed class ContentItemWorkflowJobHandler(
    AppDbContext db,
    IJobQueue jobs,
    IClock clock,
    IBrandBrainReader brandReader,
    IRendererClient renderer,
    IAssetContentResolver assetResolver,
    AgentExecutor agentExecutor,
    CopywriterAgent copywriter,
    ILogger<ContentItemWorkflowJobHandler> logger)
    : JobHandler<AdvanceContentItemWorkflowPayload>
{
    /// <summary>
    /// Belt and braces beyond <c>WorkflowRun.MaxStepsExecuted</c>: a hard cap on how many
    /// times this one invocation loops, so a bug in the decision logic cannot spin forever
    /// inside a single job even before the run's own counters would have caught it.
    /// </summary>
    private const int MaxIterationsPerInvocation = 25;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    protected override async Task HandleAsync(
        JobExecutionContext context, AdvanceContentItemWorkflowPayload payload, CancellationToken ct)
    {
        var item = await db.ContentItems.FirstOrDefaultAsync(i => i.Id == payload.ContentItemId, ct);

        if (item is null)
        {
            throw new PermanentJobFailureException($"No content item '{payload.ContentItemId}'.");
        }

        var run = await db.WorkflowRuns.FirstOrDefaultAsync(
            r => r.Scope == WorkflowScope.Item && r.EntityId == item.Id, ct);

        if (run is null)
        {
            // No run means nothing is driving this item yet, or it already finished and the
            // run row's job is done. Either way there is nothing this invocation can do.
            logger.LogInformation("Item {ItemId} has no workflow run; nothing to advance.", item.Id);
            return;
        }

        if (run.IsTerminal)
        {
            return;
        }

        var campaign = await db.ContentCampaigns.AsNoTracking().FirstOrDefaultAsync(c => c.Id == item.CampaignId, ct)
            ?? throw new PermanentJobFailureException($"Item {item.Id} references campaign '{item.CampaignId}', which does not exist.");

        run.Lease(context.JobId.ToString("N"), clock.UtcNow, TimeSpan.FromMinutes(10));
        await db.SaveChangesAsync(ct);

        if (item.Type != ContentItemType.StaticPost)
        {
            item.SendToHumanReview("Only static posts are driven by this phase's job handler; carousels and reels need their own composer.");
            run.SendToHumanReview("Item type not yet supported by the workflow handler.", clock.UtcNow);
            await db.SaveChangesAsync(ct);

            return;
        }

        var brand = await brandReader.GetVersionAsync(campaign.BrandProfileVersionId, ct);

        for (var iteration = 0; iteration < MaxIterationsPerInvocation; iteration++)
        {
            var now = clock.UtcNow;
            var attempt = item.QualityAttempts + 1;

            var decision = OrchestratorCore.Decide(new WorkflowDecisionContext
            {
                Run = run,
                CurrentStep = item.Status,
                Now = now,
                QaReport = null,
                PreviousRemediation = await LoadPreviousRemediationAsync(run.Id, ct),
                Budget = null,
            });

            var advanced = await ApplyAsync(decision, item, run, campaign, brand, attempt, now, ct);
            await db.SaveChangesAsync(ct);

            if (run.IsTerminal || !advanced)
            {
                break;
            }
        }

        run.ReleaseLease();
        await db.SaveChangesAsync(ct);

        if (!run.IsTerminal)
        {
            // Either the iteration cap was hit or a step deliberately stopped the pass
            // (Writing just spent money; a remediation restart just happened) to give the
            // run a fresh lease window and a place for the job's own retry/backoff to apply
            // if something above actually failed rather than merely paused.
            await jobs.EnqueueAsync(payload, context.TenantId, ct: ct);
        }
    }

    /// <summary>Executes one decision. Returns false when the pass should stop without being terminal.</summary>
    private async Task<bool> ApplyAsync(
        NextAction decision,
        ContentItem item,
        WorkflowRun run,
        ContentCampaign campaign,
        BrandSnapshot brand,
        int attempt,
        DateTimeOffset now,
        CancellationToken ct)
    {
        switch (decision.Kind)
        {
            case NextActionKind.Complete:
                item.Approve();
                run.Complete(now);
                return true;

            case NextActionKind.NeedsHumanReview:
                item.SendToHumanReview(decision.Reason ?? "Attempts exhausted.");
                run.SendToHumanReview(decision.Reason ?? "Attempts exhausted.", now);
                return true;

            case NextActionKind.Replan:
                // The strategist re-planning one item mid-campaign is Phase 5+8 territory
                // (CampaignWorkflow does not exist yet). Routing to human review is honest
                // about that rather than silently retrying a fix that cannot work.
                item.SendToHumanReview($"Needs a re-plan, which this phase cannot do yet: {decision.Reason}");
                run.SendToHumanReview("Escalated to replan; no campaign-level workflow exists yet to act on it.", now);
                return true;

            case NextActionKind.Remediate:
                await ApplyRemediationAsync(decision, item, run, attempt, now, ct);
                run.RecordStep();
                return false; // fresh lease window after spending a quality attempt.

            case NextActionKind.ExecuteStep:
                return await ExecuteStepAsync(decision.Step!.Value, item, run, campaign, brand, attempt, now, ct);

            default:
                throw new InvalidOperationException($"Unhandled action {decision.Kind}.");
        }
    }

    private async Task ApplyRemediationAsync(
        NextAction decision, ContentItem item, WorkflowRun run, int attempt, DateTimeOffset now, CancellationToken ct)
    {
        var remediation = decision.Remediation!;

        TransitionTo(item, ContentItemStatus.Remediating);
        item.RecordQualityAttempt();
        run.RecordQualityAttempt();

        db.ContentRevisions.Add(new ContentRevision(
            item.TenantId, item.Id, attempt, remediation.Reason,
            remediation.Outcome.ToString(), now,
            restartAtStep: remediation.RestartAt?.ToString()));

        // The (code, target) pair this attempt acted on, for the repeated-fix escalation
        // rule — recorded on the Validating step itself since that is what raised the
        // finding, so the next pass through this item can find it without a bespoke table.
        var validatingStep = new WorkflowStep(item.TenantId, run.Id, nameof(ContentItemStatus.Validating), attempt, now);
        validatingStep.Succeed(now, resultJson: JsonSerializer.Serialize(
            new PreviousRemediationRecord(remediation.Code!.Value, remediation.RestartAt), Json));
        db.WorkflowSteps.Add(validatingStep);

        TransitionTo(item, remediation.RestartAt!.Value);
    }

    private async Task<bool> ExecuteStepAsync(
        ContentItemStatus step,
        ContentItem item,
        WorkflowRun run,
        ContentCampaign campaign,
        BrandSnapshot brand,
        int attempt,
        DateTimeOffset now,
        CancellationToken ct)
    {
        run.RecordStep();
        item.RecordStep();

        switch (step)
        {
            case ContentItemStatus.Pending:
                TransitionTo(item, ContentItemStatus.Directing);
                return true;

            case ContentItemStatus.Directing:
                return await ExecuteDirectingAsync(item, run, brand, attempt, now, ct);

            case ContentItemStatus.Writing:
                return await ExecuteWritingAsync(item, run, campaign, brand, attempt, now, ct);

            case ContentItemStatus.SpecAssembly:
                return await ExecuteSpecAssemblyAsync(item, run, brand, attempt, now, ct);

            case ContentItemStatus.AssetGeneration:
                // No image generation client exists yet (see PARALLEL-WORK.md). Every
                // required asset must already be a user upload, which SpecAssembly's
                // resolver will simply fail loudly on if that is not the case.
                TransitionTo(item, ContentItemStatus.Rendering);
                return true;

            case ContentItemStatus.Rendering:
                return await ExecuteRenderingAsync(item, run, brand, attempt, now, ct);

            default:
                throw new InvalidOperationException($"{step} has no executor in this handler.");
        }
    }

    private async Task<bool> ExecuteDirectingAsync(
        ContentItem item, WorkflowRun run, BrandSnapshot brand, int attempt, DateTimeOffset now, CancellationToken ct)
    {
        var manifests = await renderer.GetManifestsAsync(ct);
        var selection = TemplateSelector.Select(manifests, item.Type, item.Pillar, brand, AspectRatio.FourFive);

        var chosen = selection.Candidates
            .OrderByDescending(c => c.PillarAffinity)
            .ThenBy(c => c.Template.TemplateId, StringComparer.Ordinal)
            .FirstOrDefault();

        if (chosen is null)
        {
            var reasons = string.Join("; ", selection.Rejections.Select(r => $"{r.TemplateId}: {r.Reason}"));
            item.SendToHumanReview($"No template can render this item. {reasons}");
            run.SendToHumanReview("No eligible template.", now);

            return false;
        }

        var result = new DirectingResult(
            chosen.Template.TemplateId,
            chosen.Template.Version,
            chosen.Ratio.ToString(),
            chosen.AssetAssignments.ToDictionary(kv => kv.Key, kv => kv.Value.Id, StringComparer.Ordinal));

        RecordStep(item, run, nameof(ContentItemStatus.Directing), attempt, now,
            resultJson: JsonSerializer.Serialize(result, Json));

        TransitionTo(item, ContentItemStatus.Writing);

        return true;
    }

    private async Task<bool> ExecuteWritingAsync(
        ContentItem item, WorkflowRun run, ContentCampaign campaign, BrandSnapshot brand, int attempt, DateTimeOffset now, CancellationToken ct)
    {
        // Reuse a prior success rather than re-billing the model on a resumed pass — the
        // one step in this loop where redoing cheap work is not actually cheap.
        var existing = await db.WorkflowSteps.AsNoTracking().FirstOrDefaultAsync(
            s => s.WorkflowRunId == run.Id && s.StepName == nameof(ContentItemStatus.Writing) &&
                 s.Attempt == attempt && s.Outcome == WorkflowStepOutcome.Succeeded, ct);

        var directing = await LoadDirectingResultAsync(run.Id, ct)
            ?? throw new PermanentJobFailureException($"Item {item.Id}: reached Writing with no recorded Directing decision.");

        var manifest = (await renderer.GetManifestAsync(directing.TemplateId, ct));
        var slots = manifest.TextSlots.Select(s => new CopySlotBrief
        {
            Id = s.Id,
            Role = s.Role,
            MaxChars = s.BudgetFor(brand.PrimaryLanguage),
            MaxLines = s.MaxLines,
        }).ToArray();

        CopySet copySet;

        if (existing is not null)
        {
            copySet = JsonSerializer.Deserialize<CopySet>(existing.ResultJson!, Json)!;
        }
        else
        {
            var input = new CopywriterInput
            {
                Brand = brand,
                Topic = item.Topic,
                Pillar = item.Pillar,
                Objective = item.Objective,
                AllowedFactKeys = brand.Facts.Select(f => f.Key).ToArray(),
                Slots = slots,
                Language = brand.PrimaryLanguage,
            };

            AgentResult<CopySet> result;

            try
            {
                result = await agentExecutor.RunAsync(copywriter, input,
                    new AgentContext { CampaignId = campaign.Id, ContentItemId = item.Id }, ct);
            }
            catch (AgentValidationException ex)
            {
                // Schema-repair is the executor's own concern (it already tried twice);
                // exhausting that here is a quality-ladder matter, handled the same way a
                // deterministic finding would be.
                RecordFailedStep(item, run, nameof(ContentItemStatus.Writing), attempt, now, ex.Message);
                item.SendToHumanReview($"The copywriter could not produce valid copy: {ex.Message}");
                run.SendToHumanReview("Copywriter validation exhausted.", now);

                return false;
            }

            copySet = result.Value;
            RecordStep(item, run, nameof(ContentItemStatus.Writing), attempt, now,
                resultJson: JsonSerializer.Serialize(copySet, Json));
        }

        TransitionTo(item, ContentItemStatus.SpecAssembly);

        return true;
    }

    private async Task<bool> ExecuteSpecAssemblyAsync(
        ContentItem item, WorkflowRun run, BrandSnapshot brand, int attempt, DateTimeOffset now, CancellationToken ct)
    {
        var directing = await LoadDirectingResultAsync(run.Id, ct)
            ?? throw new PermanentJobFailureException($"Item {item.Id}: reached SpecAssembly with no recorded Directing decision.");

        var writingStep = await db.WorkflowSteps.AsNoTracking().FirstOrDefaultAsync(
            s => s.WorkflowRunId == run.Id && s.StepName == nameof(ContentItemStatus.Writing) &&
                 s.Attempt == attempt && s.Outcome == WorkflowStepOutcome.Succeeded, ct)
            ?? throw new PermanentJobFailureException($"Item {item.Id}: reached SpecAssembly with no recorded copy.");

        var copySet = JsonSerializer.Deserialize<CopySet>(writingStep.ResultJson!, Json)!;

        var manifestPayload = await renderer.GetManifestAsync(directing.TemplateId, ct);
        var templateVersionRow = await GetOrCreateTemplateVersionAsync(manifestPayload, now, ct);

        IReadOnlyDictionary<string, ImagePayload> assets = directing.AssetAssignments.Count == 0
            ? new Dictionary<string, ImagePayload>(StringComparer.Ordinal)
            : await assetResolver.ResolveManyAsync(directing.AssetAssignments, ct);

        var ratio = Enum.Parse<AspectRatio>(directing.AspectRatio);
        var request = SpecAssembler.Assemble(manifestPayload, ratio, copySet, brand, brand.PrimaryLanguage, assets);
        var hash = SpecAssembler.ComputeHash(request);

        var spec = new CreativeSpec(
            item.TenantId, item.Id, attempt, templateVersionRow.Id,
            JsonSerializer.Serialize(request, Json), hash, now);

        db.CreativeSpecs.Add(spec);

        RecordStep(item, run, nameof(ContentItemStatus.SpecAssembly), attempt, now);
        item.MoveTo(ContentItemStatus.AssetGeneration);

        return true;
    }

    private async Task<bool> ExecuteRenderingAsync(
        ContentItem item, WorkflowRun run, BrandSnapshot brand, int attempt, DateTimeOffset now, CancellationToken ct)
    {
        var spec = await db.CreativeSpecs.AsNoTracking().FirstOrDefaultAsync(
            s => s.ContentItemId == item.Id && s.Attempt == attempt, ct)
            ?? throw new PermanentJobFailureException($"Item {item.Id}: reached Rendering with no CreativeSpec for attempt {attempt}.");

        var request = JsonSerializer.Deserialize<RenderImageRequest>(spec.SpecJson, Json)!;
        var templateVersionRow = await db.TemplateVersions.AsNoTracking().SingleAsync(t => t.Id == spec.TemplateVersionId, ct);
        var manifest = JsonSerializer.Deserialize<TemplateManifest>(templateVersionRow.ManifestJson, Json)!;

        RenderImageResponse response;

        try
        {
            response = await renderer.RenderAsync(request, ct);
        }
        catch (RenderSpecRejectedException ex)
        {
            // The renderer's own contract: this spec cannot be satisfied. Not worth a
            // transient retry — treat it as a deterministic, blocking finding and let the
            // ladder decide whether that means a different template or a human.
            var report = new QaReport
            {
                Gate = QaGate.Deterministic,
                Findings = [new QaFinding
                {
                    Code = QaFindingCode.ScreenshotAltered,
                    Severity = QaSeverity.Blocking,
                    Detail = $"The renderer rejected the spec: {ex.Message}",
                }],
            };

            return await FinishAttemptAsync(item, run, report, attempt, now, ct);
        }
        catch (RendererUnavailableException ex)
        {
            run.RecordTransientRetry();

            if (run.TransientExhausted)
            {
                RecordFailedStep(item, run, nameof(ContentItemStatus.Rendering), attempt, now, ex.Message);
                item.SendToHumanReview($"The renderer was unavailable after {run.MaxTransientAttempts} attempts: {ex.Message}");
                run.SendToHumanReview("Renderer transient retries exhausted.", now);

                return false;
            }

            // Leave the item on Rendering; the next pass (a fresh job, a fresh lease
            // window) tries again with backoff supplied by the job queue itself.
            return false;
        }

        var bytes = response.Image.ToBytes();

        db.ContentAssets.Add(new ContentAsset(
            item.TenantId, item.Id, attempt, ContentAssetKind.Image,
            $"pending/{item.Id:N}/{attempt}", response.Image.MediaType,
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)),
            bytes.Length, now, response.Report.Width, response.Report.Height));

        var fidelity = new List<CompareResult>();

        foreach (var slot in manifest.AssetSlots.Where(s => s.Immutable))
        {
            if (!request.Assets.TryGetValue(slot.Id, out var reference) || !response.Masks.TryGetValue(slot.Id, out var mask))
            {
                continue;
            }

            fidelity.Add(await renderer.CompareAsync(new CompareRequest
            {
                SlotId = slot.Id,
                Reference = reference,
                Rendered = response.Image,
                Mask = mask,
            }, ct));
        }

        var sourceAspectRatios = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var asset in brand.Assets)
        {
            if (asset.Width > 0 && asset.Height > 0)
            {
                sourceAspectRatios.TryAdd(asset.Id.ToString(), (double)asset.Width / asset.Height);
            }
        }

        var qaInput = new DeterministicQaInput
        {
            Manifest = manifest,
            AspectRatio = request.AspectRatio,
            Report = response.Report,
            Fidelity = fidelity,
            SourceAspectRatios = sourceAspectRatios,
            ExpectedFonts = [brand.Visual.HeadingFont, brand.Visual.BodyFont],
            File = new RenderedFile { Width = response.Report.Width, Height = response.Report.Height, Bytes = bytes.Length },
        };

        var qaReport = DeterministicQaSuite.Run(qaInput);

        RecordStep(item, run, nameof(ContentItemStatus.Rendering), attempt, now);
        TransitionTo(item, ContentItemStatus.Validating);

        return await FinishAttemptAsync(item, run, qaReport, attempt, now, ct);
    }

    /// <summary>
    /// Validating never gets its own pass through the loop: the report only exists in
    /// memory for the moment right after a render, so it is fed straight back into
    /// <see cref="OrchestratorCore.Decide"/> here rather than round-tripped through a
    /// second job.
    /// </summary>
    private async Task<bool> FinishAttemptAsync(
        ContentItem item, WorkflowRun run, QaReport qaReport, int attempt, DateTimeOffset now, CancellationToken ct)
    {
        db.QualityReviews.Add(qaReport.ToReview(item.TenantId, item.Id, attempt, now));

        var decision = OrchestratorCore.Decide(new WorkflowDecisionContext
        {
            Run = run,
            CurrentStep = ContentItemStatus.Validating,
            Now = now,
            QaReport = qaReport,
            PreviousRemediation = await LoadPreviousRemediationAsync(run.Id, ct),
        });

        // Complete/Remediate/Replan/NeedsHumanReview are the only outcomes Decide can
        // return once a QaReport is supplied — never ExecuteStep — so the campaign and
        // brand this call never touches are safely elided.
        return await ApplyAsync(decision, item, run, default!, default!, attempt, now, ct);
    }

    private async Task<TemplateVersion> GetOrCreateTemplateVersionAsync(TemplateManifest manifest, DateTimeOffset now, CancellationToken ct)
    {
        var existing = await db.TemplateVersions.FirstOrDefaultAsync(
            t => t.TemplateId == manifest.TemplateId && t.Version == manifest.Version, ct);

        if (existing is not null)
        {
            return existing;
        }

        var json = JsonSerializer.Serialize(manifest, Json);
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)));

        var version = new TemplateVersion(manifest.TemplateId, manifest.Version, json, hash, now);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync(ct);

        return version;
    }

    /// <summary>
    /// Directing is not re-run on every attempt — only remediation that specifically
    /// restarts at Directing produces a new decision. Everything else (a restart at Writing
    /// or later) keeps using whichever template the most recent successful Directing step
    /// chose, which is why this looks up the latest one rather than one scoped to the
    /// current attempt.
    /// </summary>
    private async Task<DirectingResult?> LoadDirectingResultAsync(Guid runId, CancellationToken ct)
    {
        var step = await db.WorkflowSteps.AsNoTracking()
            .Where(s => s.WorkflowRunId == runId && s.StepName == nameof(ContentItemStatus.Directing) &&
                        s.Outcome == WorkflowStepOutcome.Succeeded)
            .OrderByDescending(s => s.Attempt)
            .FirstOrDefaultAsync(ct);

        return step?.ResultJson is null ? null : JsonSerializer.Deserialize<DirectingResult>(step.ResultJson, Json);
    }

    private async Task<(QaFindingCode Code, ContentItemStatus Target)?> LoadPreviousRemediationAsync(Guid runId, CancellationToken ct)
    {
        var step = await db.WorkflowSteps.AsNoTracking()
            .Where(s => s.WorkflowRunId == runId && s.StepName == nameof(ContentItemStatus.Validating) && s.ResultJson != null)
            .OrderByDescending(s => s.StartedAt)
            .FirstOrDefaultAsync(ct);

        if (step?.ResultJson is null)
        {
            return null;
        }

        var record = JsonSerializer.Deserialize<PreviousRemediationRecord>(step.ResultJson, Json)!;

        return record.RestartAt is null ? null : (record.Code, record.RestartAt.Value);
    }

    /// <summary>
    /// The orchestrator asks <see cref="ItemStateMachine"/> before every transition, exactly
    /// as §6 describes: an illegal transition is a bug in this handler, not a runtime
    /// condition, so it throws rather than silently moving the item anyway.
    /// </summary>
    private static void TransitionTo(ContentItem item, ContentItemStatus next)
    {
        if (!ItemStateMachine.IsLegalTransition(item.Status, next))
        {
            throw new InvalidOperationException($"Illegal transition for item {item.Id}: {item.Status} -> {next}.");
        }

        item.MoveTo(next);
    }

    private void RecordStep(ContentItem item, WorkflowRun run, string stepName, int attempt, DateTimeOffset now, string? resultJson = null)
    {
        var step = new WorkflowStep(item.TenantId, run.Id, stepName, attempt, now);
        step.Succeed(now, resultJson: resultJson);
        db.WorkflowSteps.Add(step);
    }

    private void RecordFailedStep(ContentItem item, WorkflowRun run, string stepName, int attempt, DateTimeOffset now, string error)
    {
        var step = new WorkflowStep(item.TenantId, run.Id, stepName, attempt, now);
        step.Fail(error, now);
        db.WorkflowSteps.Add(step);
    }

    private sealed record DirectingResult(
        string TemplateId,
        int TemplateVersion,
        string AspectRatio,
        IReadOnlyDictionary<string, Guid> AssetAssignments);

    private sealed record PreviousRemediationRecord(QaFindingCode Code, ContentItemStatus? RestartAt);
}
