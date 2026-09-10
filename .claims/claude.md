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
src/ContentPilot.Application/Jobs/
src/ContentPilot.Infrastructure/Ai/
src/ContentPilot.Infrastructure/Jobs/
src/ContentPilot.Infrastructure/Persistence/Migrations/
tests/ContentPilot.UnitTests/Agents/
tests/ContentPilot.UnitTests/Ai/
tests/ContentPilot.UnitTests/Quality/
tests/ContentPilot.UnitTests/Orchestration/
tests/ContentPilot.UnitTests/Domain/

## notes

**Phase 4 landed** (deterministic QA) — see PARALLEL-WORK.md.

**Phase 5 partially landed** (orchestrator policy, not yet wired to a worker):
`WorkflowRun`/`WorkflowStep`/`BudgetReservation` in `Domain/Workflow/`, `ContentRevision` in
`Domain/Content/`, migration `Workflow` landed. `Application/Orchestration/` has
`ItemStateMachine` (§7 transition legality), `RemediationRouter` (§8 finding→step routing
and escalation ladder), `BudgetGuard` (§24 reserve-then-commit) — all pure, all unit tested,
none of it called from anywhere yet. Full detail in PARALLEL-WORK.md's phase 5 section,
including exactly what is still missing before "generate week" runs unattended.

**Still to do in this lane, in the order that makes sense:**
1. The core loop itself (§6's numbered list) — lease a `WorkflowRun`, decide the next
   action, execute one step, persist + transition + enqueue in one transaction.
2. `IJobQueue` job type(s) for `ContentItemWorkflow`; wiring the existing
   `ContentStrategistAgent`, `DeterministicQaSuite`, and a renderer HTTP client together
   through the steps `ItemStateMachine` already knows the shape of.
3. `CampaignWorkflow` (campaign-level: plan → fan out items → package).
4. The manual trigger endpoint and the Hangfire weekly cron.
5. Image generation has no client at all yet — needed before `AssetGeneration` can do
   anything beyond passing through user-uploaded assets.

Holds `migrations: true`. Nobody else runs `dotnet ef migrations add`. Add entities and
configuration, skip the migration, and say so in PARALLEL-WORK.md — same as phases 3 and 4.
