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

**Phase 5 partially landed** — orchestrator policy, a renderer client, every domain entity
§12 calls for around the item pipeline, and now a copywriter agent. Still not wired to a
worker:

- `Domain/Workflow/`: `WorkflowRun`, `WorkflowStep`, `BudgetReservation`. `ContentRevision`,
  `TemplateVersion`, `CreativeSpec`, `ContentAsset` in `Domain/Content/`. Migrations
  `Workflow` and `CreativePipeline` landed.
- `Application/Orchestration/`: `ItemStateMachine`, `RemediationRouter`, `BudgetGuard`,
  `OrchestratorCore.Decide` (the `(WorkflowRun, WorkflowStep[]) => NextAction` function §6
  names directly).
- `Application/Abstractions/IRendererClient.cs` + `Infrastructure/Rendering/
  HttpRendererClient.cs`: the typed door to the Renderer service.
- `Application/Agents/CopywriterAgent.cs` + `CopyValidator.cs` + `CopySet.cs`: Writing's
  executor, same `IAgent<,>` shape as `ContentStrategistAgent`. Uses the `copywriter` model
  profile that already existed in `appsettings.json` but was never used until now.

All of it is pure or independently unit tested; none of it is called from anywhere yet.
Full detail across PARALLEL-WORK.md's several phase 5 sections.

**Every step but SpecAssembly now has a real executor**: Directing (`TemplateSelector`),
Writing (`CopywriterAgent`), Rendering (`IRendererClient`), Validating
(`DeterministicQaSuite` + `IRendererClient.CompareAsync`). SpecAssembly is now pure glue —
building a `RenderImageRequest` from a `TemplateManifest`, a `CopySet` and the brand's
tokens/assets, all of which already exist as types — with no missing dependency behind it.

**Still to do in this lane, in the order that makes sense:**
1. SpecAssembly itself: template + `CopySet` + `BrandTokens`/`AssetView` → `RenderImageRequest`,
   hashed and persisted as a `CreativeSpec`.
2. The caller: lease a `WorkflowRun`, call `OrchestratorCore.Decide`, execute the named
   step, persist the step/transition/next-job enqueue in one transaction. The only
   remaining piece of §6's own numbered list.
3. `IJobQueue` job type(s) for `ContentItemWorkflow`, wiring everything above through the
   steps `ItemStateMachine` already knows the shape of.
4. `CampaignWorkflow` (campaign-level: plan → fan out items → package).
5. The manual trigger endpoint and the Hangfire weekly cron.
6. Image generation has no client at all yet — needed before `AssetGeneration` can do
   anything beyond passing through user-uploaded assets.

Holds `migrations: true`. Nobody else runs `dotnet ef migrations add`. Add entities and
configuration, skip the migration, and say so in PARALLEL-WORK.md — same as phases 3 and 4.
