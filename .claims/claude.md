---
agent: claude
phase: 5
branch: main
status: active
migrations: true
updated: 2026-09-10
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
src/ContentPilot.Infrastructure/Ai/
src/ContentPilot.Infrastructure/Persistence/Migrations/
tests/ContentPilot.UnitTests/Agents/
tests/ContentPilot.UnitTests/Ai/
tests/ContentPilot.UnitTests/Quality/

## notes

**Phase 4 landed** (deterministic QA): `QaFindingCode`/`QaSeverity`/`QaGate`/`QaOutcome`
in `Domain/Quality/` (append-only, grouped by hundreds, gate ownership enforced by
`QualityReview`'s constructor), `DeterministicQaSuite` in `Application/Quality/` turning the
renderer's `RenderReport` and `/compare` output into findings, and a calibration harness in
`tests/ContentPilot.RendererTests/FidelityCalibrationTests.cs` that regenerates
`artifacts/fidelity-calibration.md`. Migration `QualityReviews` landed. Full details in
PARALLEL-WORK.md's phase 4 section.

Now on **phase 5 — orchestrator, retries and self-correction**. Read IMPLEMENTATION-PLAN.md
§6–8 and §30–31 phase 5 (line 1266) before starting. `QaFinding`/`QaGate`/`QaOutcome` from
phase 4 are the vocabulary the `RemediationRouter` switches on.

Holds `migrations: true`. Nobody else runs `dotnet ef migrations add`. `WorkflowRun`,
`WorkflowStep`, `ContentRevision` and `BudgetReservation` will need one — write the entities
and configuration, then stop and say so in PARALLEL-WORK.md before generating it, the same
way phase 3 and phase 4 did.
