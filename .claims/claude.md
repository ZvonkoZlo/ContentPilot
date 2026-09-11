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
tests/ContentPilot.UnitTests/Agents/
tests/ContentPilot.UnitTests/Ai/
tests/ContentPilot.UnitTests/Quality/
tests/ContentPilot.UnitTests/Orchestration/
tests/ContentPilot.UnitTests/Domain/
tests/ContentPilot.UnitTests/Rendering/
tests/ContentPilot.IntegrationTests/AssetContentResolverTests.cs
tests/ContentPilot.IntegrationTests/ContentItemWorkflowJobHandlerTests.cs
tests/ContentPilot.WorkflowTests/

## notes

**Phase 4 landed** (deterministic QA) — see PARALLEL-WORK.md.

**Phase 5: the loop actually runs.** `ContentItemWorkflowJobHandler`
(`Infrastructure/Jobs/`) is the caller from §6: leases nothing itself, loads a
`WorkflowRun`, calls `OrchestratorCore.Decide`, executes the named step, persists the
result, re-enqueues (job type `advance-content-item-workflow`). Proven end to end by two
integration tests against real Postgres/MinIO — a clean render reaches `Approved` with real
`CreativeSpec`/`ContentAsset`/`QualityReview` rows; a render that always overflows converges
to `NeedsHumanReview` through the actual escalation ladder, not just a unit-tested policy.

Everything from earlier in this phase is now actually called from somewhere:
`ItemStateMachine` (now enforced via a `TransitionTo` helper before every `item.MoveTo`,
not only tested), `RemediationRouter`, `OrchestratorCore.Decide`, `IRendererClient`,
`IAssetContentResolver`, `SpecAssembler`, `CopywriterAgent`, `TemplateSelector`,
`DeterministicQaSuite`. Migration `WorkflowStepResult` added `WorkflowStep.ResultJson` (a
compact per-step decision record — Directing's chosen template, Writing's `CopySet` — so a
crash mid-pass does not redo billable work or lose a decision a later step needs).

**Read PARALLEL-WORK.md's phase 5 sections in full before touching this**, especially the
"one simplification worth knowing" (steps loop inside one job invocation rather than each
getting its own queue round-trip) and the JobDispatcher gotcha it surfaced (every registered
`IJobHandler` is constructed on every dispatch attempt — a rich dependency chain on any job
type can break dispatch of every job type in a host missing that chain's config; fixed for
`PingWalkingSkeletonTests`, worth remembering for the next job type someone adds).

**Explicit, stated scope — not silently missing, just not built:**
- Only `StaticPost` items are driven; carousels/reels go straight to `NeedsHumanReview`.
- No budget enforcement (`WorkflowDecisionContext.Budget` always null) — attempt/step
  ceilings apply, cost ceilings do not yet.
- `AssetGeneration` is a pass-through — no image generation client exists.
- `Replan` goes to `NeedsHumanReview` — `CampaignWorkflow` does not exist to act on it.

**What's actually left for "generate week" to run unattended:**
1. `CampaignWorkflow` — nothing creates a `WorkflowRun` or enqueues the first job for an
   item yet. This handler has no caller in the running system, only in its own tests.
2. The manual trigger endpoint and the Hangfire weekly cron.
3. Budget reservation and enforcement (§24), wired into the decision context.
4. Carousel and reel composition; image generation.

Holds `migrations: true`. Nobody else runs `dotnet ef migrations add`. Add entities and
configuration, skip the migration, and say so in PARALLEL-WORK.md — same as phases 3 and 4.
