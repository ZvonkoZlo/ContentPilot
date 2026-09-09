# Working in parallel on ContentPilot

This file is the coordination contract between agents working on this repository at the
same time. Read it before touching anything.

The build order in [IMPLEMENTATION-PLAN.md](IMPLEMENTATION-PLAN.md) §37 exists because the
phases genuinely depend on each other. Some of them can still run side by side; most
cannot. This document says which, and names the exact files where two agents would collide.

**Realistic expectation:** two agents on well-chosen phases finish maybe 30–40% sooner, not
twice as fast. Coordination is not free. Picking the wrong second phase makes it *slower*.

---

## Current assignment

| Phase | Scope | Owner | State |
|---|---|---|---|
| 0 | Foundations, tenancy, job queue, storage, telemetry | — | Done |
| 1 | Brand Brain, asset library | — | Done |
| 2 | Renderer, five static templates, screenshot fidelity | — | Done |
| **3** | **Text agents and the LLM layer** | **Claude (primary)** | **In progress** |
| 7 | Reels: scene composer, FFmpeg, reel templates | **free — best second phase** | Not started |
| 4 | Deterministic QA suite | free — good second phase | Not started |
| 8 | Packaging, ZIP, email, review UI | free — acceptable | Not started |
| 5 | Orchestrator, retries, self-correction | **blocked** — needs 3 and 4 | Not started |
| 6 | Visual QA and Marketing QA agents | **blocked** — needs 3 and 4 | Not started |
| 9 | Hardening, evals, cost calibration | **blocked** — needs real usage data | Not started |

Update the Owner column when you pick something up. One owner per phase.

---

## Ownership map

A directory has exactly one owner while its phase is in progress. Do not edit outside your
lane, even to fix something obvious — open an issue in your handover notes instead.

| Path | Owner | Notes |
|---|---|---|
| `src/ContentPilot.Domain/Content/` | Phase 3 | Campaign, item, history |
| `src/ContentPilot.Domain/Observability/` | Phase 3 | AgentRun, PromptVersion, CostEntry |
| `src/ContentPilot.Application/Agents/` | Phase 3 | |
| `src/ContentPilot.Application/Prompts/` | Phase 3 | |
| `src/ContentPilot.Application/ContentMemory/` | Phase 3 | |
| `src/ContentPilot.Infrastructure/Ai/` | Phase 3 | |
| `src/ContentPilot.Application/Quality/` | Phase 4 | Findings, deterministic checks |
| `src/ContentPilot.Renderer/Video/` | Phase 7 | Scene composer, FFmpeg |
| `src/ContentPilot.Renderer/Templates/reel/` | Phase 7 | |
| `src/ContentPilot.Application/Campaigns/` | Phase 8 | Packaging use cases |
| `src/ContentPilot.Infrastructure/Email/` | Phase 8 | |
| `src/ContentPilot.Application/Orchestration/` | Phase 5 | Nobody yet |

Everything not listed — `Domain/Branding`, `Domain/Tenancy`, `Domain/Common`,
`Infrastructure/Persistence`, `Infrastructure/Storage`, `Infrastructure/Jobs`,
`Infrastructure/Branding`, `Application/Brand`, `Application/Capabilities` — is **finished
and frozen**. Changing it means changing something two other phases already depend on. If
you genuinely need a change there, say so in your handover notes and let the other agent
land it.

---

## The four real collision points

### 1. EF migrations — the one that actually hurts

**Only the primary agent (Phase 3) runs `dotnet ef migrations add`.**

Two migrations generated in parallel both branch from the same model snapshot. Merging them
produces a chain EF cannot linearise, and the fix is deleting both and regenerating — losing
whatever was hand-edited in them.

If your phase adds an entity:

1. Write the entity and its `IEntityTypeConfiguration` normally.
2. Add the `DbSet` to `AppDbContext` (see rule 2).
3. **Stop there.** Do not generate the migration. Note it in your handover.
4. The migration owner generates one migration covering everything, after the merge.

Your integration tests will not run until that migration exists. Write them anyway; they go
green when the schema lands.

### 2. Three files everyone touches

- `src/ContentPilot.Infrastructure/Persistence/AppDbContext.cs` — the `DbSet` block
- `src/ContentPilot.Infrastructure/DependencyInjection.cs` — service registration
- `src/ContentPilot.Api/Program.cs` — endpoint mapping

These are unavoidable and the conflicts are small, but they are *constant*. Rules:

- **Append only.** Add your line at the end of the relevant block. Never reorder, never
  reformat, never "tidy up while I'm here" — a whitespace change turns a one-line conflict
  into a whole-file one.
- **One line per phase where possible.** Put your registrations in your own file
  (`AddContentPilotAgents()`, `AddContentPilotQuality()`) and call it from
  `DependencyInjection.cs`. Same for endpoints: your own `MapXEndpoints()` in
  `src/ContentPilot.Api/Endpoints/`, one `app.MapXEndpoints();` line in `Program.cs`.

### 3. `ContentPilot.Rendering.Contracts` is append-only

Phase 4 reads it; Phase 7 extends it. Both at once is fine **only** under this rule:

- New types go in **new files**. `ReelContracts.cs`, not appended to `RenderContracts.cs`.
- Never modify an existing record, enum or property. Phase 2's renderer, its 20 tests and
  the calibrated fidelity thresholds all depend on the current shapes.
- Need a field on an existing type? Add it as nullable with a default, or make a new type.

`QaFindingCode` lives in `Application/Quality/`, **not** in Rendering.Contracts, precisely so
Phases 4 and 7 do not meet.

### 4. Shared test projects

`tests/ContentPilot.UnitTests` and `tests/ContentPilot.IntegrationTests` are shared. Files
are not a problem — put yours in your own folder (`UnitTests/Quality/`, `UnitTests/Video/`).

**The `.csproj` is a problem.** Adding a NuGet package edits a file the other agent also
edits. Announce package additions in your handover notes before making them.

---

## Environment rules

The stack is not designed for two people running it at once. Before you start:

```bash
# Every agent picks its own compose project, or you will fight over containers.
export COMPOSE_PROJECT_NAME=contentpilot-<yourname>

# And its own published ports, in a git-ignored .env
POSTGRES_PORT=5434        # 5433 is the default; the host already runs PostgreSQL 16 on 5432
MINIO_PORT=9010
MINIO_CONSOLE_PORT=9011
```

- **Never run `docker compose down -v`.** It destroys volumes the other agent is using. Use
  `docker compose down` without `-v`, or `docker compose stop`.
- **Watch the disk.** The renderer image is ~2.7 GB and Docker's build cache grows fast. A
  full disk has already taken this project down once — it killed the Docker daemon mid-build
  and truncated a file that was being written. Check free space before
  `docker compose build`, and run `docker builder prune -af` afterwards.
- **Testcontainers** creates its own throwaway containers per test run and does not clash.
- If the Docker daemon is older than v25, prefix test runs with `DOCKER_API_VERSION=1.43`.

### If the repository moves to another disk

Everything in the repo uses relative paths and will move cleanly, with two exceptions:

- `.playwright/` (Chromium, ~700 MB) is git-ignored. Either copy it or reinstall:
  `PLAYWRIGHT_BROWSERS_PATH=./.playwright ./src/ContentPilot.Renderer/bin/Debug/net10.0/playwright.ps1 install chromium`
- Docker volumes live in the Docker VM, not the repo. They survive the move; the data in
  them is throwaway anyway (`--migrate` plus `--seed` rebuilds it).

---

## Git

```bash
git checkout -b phase-7-reels        # one branch per phase, named for it
```

- **Commit often.** Two agents editing the same file with uncommitted work is not a merge
  conflict, it is lost code.
- **Rebase onto `main` before you open the merge**, so you resolve your own conflicts rather
  than handing them to the other agent.
- Never rebase or force-push a branch you do not own.
- `main` must always build and stay green. If your merge breaks it, fix it or revert it —
  do not leave it broken while you investigate.

---

## What "done" means for a parallel phase

Before merging into `main`:

1. `dotnet build` clean — the solution treats warnings as errors, so there is no grey area.
2. Your own test suites green, and **every suite you did not write still green**. Run them
   all; a change in your lane that breaks someone else's tests is your problem to find.
3. `dotnet test tests/ContentPilot.ArchitectureTests` green. These enforce the layering and
   the rule that agents cannot reach persistence, storage or the queue. If one fails, do not
   relax the rule — the rule is the point.
4. No `dotnet ef migrations add` in your diff unless you are the migration owner.
5. Handover notes updated (below).

---

## Handover notes

Append to this section as you go. This is how the other agent finds out what changed under
them without reading the whole diff.

### Phase 3 — text agents and the LLM layer (Claude, in progress)

**Landed so far.** Content and observability entities: `ContentCampaign` (state machine
that throws on an illegal transition), `ContentItem` (three independent attempt counters —
quality, schema repair, transient — because a provider 429 is not a quality failure),
`ContentHistoryEntry` (carries SimHash fields so novelty is a computation, not a request the
model may ignore), `AgentRun`, `PromptVersion`, `CostEntry`.

**Provider decision.** The official Anthropic C# SDK (`Anthropic` package), model
`claude-opus-5`, behind our own `ILanguageModelClient` in `Application/Abstractions`. This
is a deliberate departure from IMPLEMENTATION-PLAN.md §18, which proposed building on
`Microsoft.Extensions.AI.IChatClient`: that abstraction flattens structured outputs,
adaptive thinking and effort, which are exactly what the agents depend on. Swapping
providers remains one adapter.

**Migration landed.** `ContentAndObservability` covers content_campaigns, content_items,
content_history, agent_runs, cost_entries and prompt_versions. From here on: add your
configuration, skip the migration, tell me.

**Shared files touched.** One `DbSet` block appended in `AppDbContext`, and one line in
`DependencyInjection` calling `AddContentPilotAi`. Both append-only, nothing reordered.

**New package.** `Anthropic` 12.46.0 in `ContentPilot.Infrastructure`. Note: that release
has no `Effort.XHigh` — the abstraction keeps the level and maps it down to `High`, which is
the conservative direction. Remove the branch when the SDK carries it.

**Coming next in this lane.** `Infrastructure/Ai/` (client plus cost, budget, retry and
cassette middleware), `Application/Prompts/`, `Application/Agents/`,
`Application/ContentMemory/`.

### Phase 7 — reels (unassigned)

Read IMPLEMENTATION-PLAN.md §17 before starting. The short version: **do not use Remotion.**
Render PNG layers through the existing Playwright pipeline, then compose them with a single
FFmpeg filtergraph. This reuses the whole template and QA stack, needs no Node runtime, and
has no licence question.

FFmpeg is already installed in `docker/Dockerfile.renderer`.

The template manifest system is the contract to work within — a reel template declares
scenes and per-scene slot budgets exactly as a static template declares slots. Reel QA runs
on extracted keyframes through the existing image checks, plus container-level checks
(duration, fps, no black frames, audio peak). Never send video to a vision model.

### Phase 4 — deterministic QA (unassigned)

Read IMPLEMENTATION-PLAN.md §9 and §10. Most of what this phase needs already exists: the
renderer returns a full measurement report (per-slot boxes, overflow, contrast against the
sampled backdrop, occlusion from the mask render) and `/compare` implements the fidelity
check with thresholds calibrated against a real mutation corpus.

The work is turning those measurements into `QaFinding` values against a **closed enum**.
That enum is the load-bearing part: if findings can be free text, remediation has to be
decided by another model call, and the orchestrator loses control of the loop.

The `QualityReview` entity needs a migration — coordinate.
