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
src/ContentPilot.Infrastructure/Jobs/
src/ContentPilot.Infrastructure/Rendering/
src/ContentPilot.Infrastructure/Persistence/Migrations/
tests/ContentPilot.UnitTests/Agents/
tests/ContentPilot.UnitTests/Ai/
tests/ContentPilot.UnitTests/Quality/
tests/ContentPilot.UnitTests/Orchestration/
tests/ContentPilot.UnitTests/Domain/
tests/ContentPilot.UnitTests/Rendering/

## notes

**Phase 4 landed** (deterministic QA) — see PARALLEL-WORK.md.

**Phase 5 partially landed** (all the orchestrator policy, a renderer client, and now every
domain entity §12 calls for around the item pipeline — still not wired to a worker):

- `Domain/Workflow/`: `WorkflowRun`, `WorkflowStep`, `BudgetReservation`. `ContentRevision`,
  `TemplateVersion`, `CreativeSpec`, `ContentAsset` in `Domain/Content/`. Migrations
  `Workflow` and `CreativePipeline` landed.
- `Application/Orchestration/`: `ItemStateMachine` (§7 transition legality),
  `RemediationRouter` (§8 finding→step routing and escalation ladder), `BudgetGuard` (§24
  reserve-then-commit), `OrchestratorCore.Decide` (the `(WorkflowRun, WorkflowStep[]) =>
  NextAction` function §6 names directly).
- `Application/Abstractions/IRendererClient.cs` + `Infrastructure/Rendering/
  HttpRendererClient.cs`: the typed door to the Renderer service.

All of it is pure or independently unit tested; none of it is called from anywhere yet.
Full detail across PARALLEL-WORK.md's several phase 5 sections.

**Still to do in this lane, in the order that makes sense — this is now entirely glue, not
new policy or new entities:**
1. The caller: lease a `WorkflowRun`, call `OrchestratorCore.Decide`, execute the named
   step, persist the step/transition/next-job enqueue in one transaction. The only
   remaining piece of §6's own numbered list.
2. `IJobQueue` job type(s) for `ContentItemWorkflow`; wiring `ContentStrategistAgent`,
   `TemplateSelector`, `IRendererClient` and `DeterministicQaSuite` together through the
   steps `ItemStateMachine` already knows the shape of, persisting `CreativeSpec` and
   `ContentAsset` rows as it goes. A copywriter agent for Writing and a spec assembler that
   turns a template + brief + brand tokens into a `RenderImageRequest` for SpecAssembly do
   not exist yet — Directing, Rendering and Validating now have real executors to call.
3. `CampaignWorkflow` (campaign-level: plan → fan out items → package).
4. The manual trigger endpoint and the Hangfire weekly cron.
5. Image generation has no client at all yet — needed before `AssetGeneration` can do
   anything beyond passing through user-uploaded assets.

Holds `migrations: true`. Nobody else runs `dotnet ef migrations add`. Add entities and
configuration, skip the migration, and say so in PARALLEL-WORK.md — same as phases 3 and 4.
