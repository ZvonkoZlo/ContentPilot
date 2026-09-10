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

**Phase 5 partially landed** (orchestrator policy plus a renderer client, still not wired to
a worker): `WorkflowRun`/`WorkflowStep`/`BudgetReservation` in `Domain/Workflow/`,
`ContentRevision` in `Domain/Content/`, migration `Workflow` landed.
`Application/Orchestration/` has `ItemStateMachine` (§7 transition legality),
`RemediationRouter` (§8 finding→step routing and escalation ladder), `BudgetGuard` (§24
reserve-then-commit), and `OrchestratorCore.Decide` — the `(WorkflowRun, WorkflowStep[]) =>
NextAction` function §6 names directly. `Application/Abstractions/IRendererClient.cs` plus
`Infrastructure/Rendering/HttpRendererClient.cs` is the typed door to the Renderer service,
registered in the shared `DependencyInjection.cs`. All of it is pure or independently unit
tested; none of it is called from anywhere yet. Full detail in PARALLEL-WORK.md's phase 5
sections.

**Still to do in this lane, in the order that makes sense:**
1. The caller: lease a `WorkflowRun`, call `OrchestratorCore.Decide`, execute the named
   step, persist the step/transition/next-job enqueue in one transaction. This is the only
   remaining piece of §6's own numbered list.
2. `IJobQueue` job type(s) for `ContentItemWorkflow`; wiring `ContentStrategistAgent`,
   `TemplateSelector`, `IRendererClient` and `DeterministicQaSuite` together through the
   steps `ItemStateMachine` already knows the shape of. A copywriter agent for Writing and a
   spec assembler that turns a `CreativeSpec` into a `RenderImageRequest` for SpecAssembly
   do not exist yet — Directing, Rendering and Validating now have real executors to call;
   Writing and SpecAssembly still do not.
3. `CampaignWorkflow` (campaign-level: plan → fan out items → package).
4. The manual trigger endpoint and the Hangfire weekly cron.
5. Image generation has no client at all yet — needed before `AssetGeneration` can do
   anything beyond passing through user-uploaded assets.

Holds `migrations: true`. Nobody else runs `dotnet ef migrations add`. Add entities and
configuration, skip the migration, and say so in PARALLEL-WORK.md — same as phases 3 and 4.
