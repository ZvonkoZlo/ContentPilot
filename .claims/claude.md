---
agent: claude
phase: 6
branch: main
status: active
migrations: false
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

**Phase 5 done** — see PARALLEL-WORK.md for the full history.

**Now on phase 6 — Visual QA and Marketing QA**, per the plan's own recommended order
(§36–37: added on top of a working loop, before Phase 8). Read IMPLEMENTATION-PLAN.md §9
(gates 2 and 3), §27 (eval scenarios), and phase 6 itself (line 1285) before touching this.

First piece landed: `ILanguageModelClient` now supports attaching images to a call
(`LlmRequest.Images`, `IAgent<,>.BuildImages`) — needed before a VisualQA agent can exist at
all, since every agent so far has been text-only. Both provider adapters updated. No
behaviour change for existing agents (default empty).

**Not yet built:** `VisualQaAgent`, `MarketingQaAgent`, their prompts and validators, the
6xx/7xx `QaFindingCode` bands are already reserved in `Domain/Quality/QaFinding.cs` from
Phase 4 waiting for exactly this. Parallel gate execution and finding merge with the
deterministic gate. The QA pass-rate metric §9 calls for (>60% first-attempt pass, treat a
false-positive rate over 0.15 as a blocking regression — §27's own numbers). Carousel
continuity checks are moot until carousels are actually driven (Phase 5 only drives
StaticPost items).

`migrations: false` — this phase adds no tables (`QualityReview` and `QaFinding` already
carry everything a model-gate finding needs).
