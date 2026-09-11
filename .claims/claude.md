---
agent: claude
phase: 5
branch: main
status: active
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
src/ContentPilot.Infrastructure/Ai/
src/ContentPilot.Infrastructure/Branding/
src/ContentPilot.Infrastructure/Jobs/
src/ContentPilot.Infrastructure/Rendering/
src/ContentPilot.Infrastructure/Persistence/Migrations/
src/ContentPilot.Api/Endpoints/CampaignEndpoints.cs
tests/ContentPilot.UnitTests/Agents/
tests/ContentPilot.UnitTests/Ai/
tests/ContentPilot.UnitTests/Quality/
tests/ContentPilot.UnitTests/Orchestration/
tests/ContentPilot.UnitTests/Domain/
tests/ContentPilot.UnitTests/Rendering/
tests/ContentPilot.IntegrationTests/AssetContentResolverTests.cs
tests/ContentPilot.IntegrationTests/ContentItemWorkflowJobHandlerTests.cs
tests/ContentPilot.IntegrationTests/CampaignWorkflowJobHandlerTests.cs
tests/ContentPilot.WorkflowTests/

## notes

**Phase 4 landed** (deterministic QA) — see PARALLEL-WORK.md.

**Phase 5: both halves of the loop run for real now.** `ContentItemWorkflowJobHandler`
drives one item Directing → Validating. `CampaignWorkflowJobHandler` (job type
`advance-campaign-workflow`) plans a campaign, fans it into items + their own
`WorkflowRun`s, and closes the campaign out once every item is terminal —
`Ready`/`PartiallyReady`/`Failed`, all via `ContentCampaign`'s own already-legal-transition-
enforcing methods. `POST /api/campaigns` (`Api/Endpoints/CampaignEndpoints.cs`) is the
manual trigger from §21; `GET .../items` is the review read side. **A `StaticPost`-only
brand triggered through that endpoint can now go from an API call to an approved, rendered,
QA-clean image with no further input** — the first time that sentence has been true in this
codebase. Full detail, including everything explicitly out of scope, across
PARALLEL-WORK.md's phase 5 sections — there are several; read all of them, oldest first.

**What's left for "generate week" to run fully unattended:**
1. The Hangfire weekly cron — the trigger path exists, nothing calls it on a schedule.
2. Budget reservation and enforcement (§24) wired into `WorkflowDecisionContext.Budget`.
3. Carousel and reel composition; image generation (no client exists).
4. Real packaging (Phase 8) — campaign completion currently skips straight past it.

**One gotcha worth remembering for the next job type**, already hit and fixed once:
`JobDispatcher.ExecuteAsync` resolves every registered `IJobHandler` on every dispatch
attempt. A job type with a rich dependency chain (this phase's two handlers both need
`AgentExecutor` → `ILanguageModelClient` → `ModelProfileRegistry`, which refuses to
construct with zero configured profiles) can break dispatch of *every* job type in a host
whose configuration does not fully satisfy that chain — not just its own. Real
`appsettings.json` always has profiles, so production is unaffected; a minimal test host
is not, and `PingWalkingSkeletonTests` needed a one-line fix for exactly this.

Holds `migrations: true`. Nobody else runs `dotnet ef migrations add`. Add entities and
configuration, skip the migration, and say so in PARALLEL-WORK.md — same as phases 3 and 4.
