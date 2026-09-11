---
agent: claude
phase: 5
branch: main
status: done
migrations: true
updated: 2026-09-11
---

## paths

src/ContentPilot.Domain/Content/
src/ContentPilot.Domain/Observability/
src/ContentPilot.Domain/Quality/
src/ContentPilot.Domain/Workflow/
src/ContentPilot.Application/Ai/
src/ContentPilot.Application/Agents/
src/ContentPilot.Application/Prompts/
src/ContentPilot.Application/ContentMemory/
src/ContentPilot.Application/Quality/
src/ContentPilot.Application/Orchestration/
src/ContentPilot.Application/Jobs/
src/ContentPilot.Application/Campaigns/
src/ContentPilot.Infrastructure/Ai/
src/ContentPilot.Infrastructure/Branding/
src/ContentPilot.Infrastructure/Campaigns/
src/ContentPilot.Infrastructure/Jobs/
src/ContentPilot.Infrastructure/Rendering/
src/ContentPilot.Infrastructure/Persistence/Migrations/
src/ContentPilot.Api/Endpoints/CampaignEndpoints.cs
src/ContentPilot.Worker/Program.cs
tests/ContentPilot.UnitTests/Agents/
tests/ContentPilot.UnitTests/Ai/
tests/ContentPilot.UnitTests/Quality/
tests/ContentPilot.UnitTests/Orchestration/
tests/ContentPilot.UnitTests/Domain/
tests/ContentPilot.UnitTests/Rendering/
tests/ContentPilot.UnitTests/Campaigns/
tests/ContentPilot.IntegrationTests/AssetContentResolverTests.cs
tests/ContentPilot.IntegrationTests/ContentItemWorkflowJobHandlerTests.cs
tests/ContentPilot.IntegrationTests/CampaignWorkflowJobHandlerTests.cs
tests/ContentPilot.IntegrationTests/CampaignTriggerJobHandlerTests.cs
tests/ContentPilot.WorkflowTests/

## notes

**Phase 4 done** (deterministic QA). **Phase 5 done**, with one named, reasoned exception —
see below. Releasing this claim; PARALLEL-WORK.md carries the full history across many
sections, oldest first, if any of this needs re-deriving later.

**What phase 5 shipped.** Two self-driving job handlers run the whole thing:
`ContentItemWorkflowJobHandler` (Directing → Validating for one `StaticPost` item, the
escalation ladder, §24 budget enforcement on Writing, best-attempt promotion on
`NeedsHumanReview`, proven crash-resumable) and `CampaignWorkflowJobHandler` (plans with the
strategist, fans out items + their own `WorkflowRun`s, closes the campaign out via its own
already-legal-transition-enforcing methods). `POST /api/campaigns` is the manual trigger
from §21; `POST /api/campaigns/{id}/cancel` stops a campaign that has not finished (items
already in flight are not reached — see PARALLEL-WORK.md for why that scope was chosen).
`CampaignTriggerScanJobHandler` (hourly, per-brand-timezone) and
`CampaignTriggerReconcileJobHandler` (daily safety net) are §21's scheduled and
disaster-recovery triggers, built on the existing job queue rather than a new Hangfire
dependency — a deliberate, documented substitution, not an oversight. **A `StaticPost`-only
brand can now go from a trigger — manual, scheduled, or reconciled — to an approved,
rendered, QA-clean image with no further human input.**

**The one thing still out of scope, on purpose:** background image generation with seeds
and variant caps. No provider is chosen and no client exists; it is its own vertical, not an
extension of anything already built. Its absence degrades gracefully rather than silently —
`TemplateSelector` only offers a template whose required assets already exist as uploads, so
an item without one simply has fewer eligible templates, never a stuck pipeline. Carousel
and reel composition and real packaging remain Phase 6/7/8, as the plan's own phase 5
feature list never included them.

**Two things worth remembering if this lane is picked up again:**
1. `JobDispatcher.ExecuteAsync` resolves every registered `IJobHandler` on every dispatch
   attempt — a job type with a rich dependency chain can break dispatch of every job type in
   a host whose config does not satisfy that chain, not just its own. Hit once
   (`PingWalkingSkeletonTests`), fixed with a minimal `Ai:Profiles` entry.
2. `Directory.Build.props` sets `InvariantGlobalization=true` solution-wide. IANA timezone
   ids resolve fine on Linux (the real deployment target) but can throw
   `TimeZoneNotFoundException` on a Windows dev box without ICU — caught and skipped
   per-brand in the trigger handlers, but worth knowing before debugging "why didn't my
   local cron fire" from scratch.

No `migrations: true` claim needed going forward unless the next phase adds entities —
whoever picks up phase 6, 7, or 8 should claim migrations fresh rather than assume this one
still holds it.
