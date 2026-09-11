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

## notes

**Phase 4 landed** (deterministic QA) — see PARALLEL-WORK.md.

**Phase 5 partially landed** — orchestrator policy, a renderer client, every domain entity
§12 calls for around the item pipeline, and now every step's executor with the I/O each one
needs to actually run. Still not wired to a worker:

- `Domain/Workflow/`: `WorkflowRun`, `WorkflowStep`, `BudgetReservation`. `ContentRevision`,
  `TemplateVersion`, `CreativeSpec`, `ContentAsset` in `Domain/Content/`. Migrations
  `Workflow` and `CreativePipeline` landed.
- `Application/Orchestration/`: `ItemStateMachine`, `RemediationRouter`, `BudgetGuard`,
  `OrchestratorCore.Decide` (the `(WorkflowRun, WorkflowStep[]) => NextAction` function §6
  names directly).
- `Application/Abstractions/IRendererClient.cs` + `Infrastructure/Rendering/
  HttpRendererClient.cs`: the typed door to the Renderer service.
- `Application/Agents/CopywriterAgent.cs` + `CopyValidator.cs` + `CopySet.cs`: Writing's
  executor.
- `Application/Agents/SpecAssembler.cs`: SpecAssembly's assembler.
- `Application/Abstractions/IAssetContentResolver.cs` + `Infrastructure/Branding/
  AssetContentResolver.cs`: resolves an asset id to the `ImagePayload` bytes `SpecAssembler`
  needs — the one piece of I/O SpecAssembly required, now covered by an integration test
  against real Postgres and MinIO.

All of it is pure or independently tested; none of it is called from anywhere yet. Full
detail across PARALLEL-WORK.md's several phase 5 sections.

**Nothing about SpecAssembly, or any earlier step, is missing any more** — no agent, no
entity, no client, no I/O gap. What remains is entirely the job handler that holds it all
together:

1. The caller: lease a `WorkflowRun`, call `OrchestratorCore.Decide`, execute the named step
   by calling whichever executor above matches it, persist the step/transition/next-job
   enqueue in one transaction. The only remaining piece of §6's own numbered list.
2. `IJobQueue` job type(s) for `ContentItemWorkflow`, wiring everything through the steps
   `ItemStateMachine` already knows the shape of.
3. `CampaignWorkflow` (campaign-level: plan → fan out items → package).
4. The manual trigger endpoint and the Hangfire weekly cron.
5. Image generation has no client at all yet — needed before `AssetGeneration` can do
   anything beyond passing through user-uploaded assets.

Holds `migrations: true`. Nobody else runs `dotnet ef migrations add`. Add entities and
configuration, skip the migration, and say so in PARALLEL-WORK.md — same as phases 3 and 4.
