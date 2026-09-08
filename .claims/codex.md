---
agent: codex
phase: 7
branch: phase-7-reels
status: active
migrations: false
updated: 2026-09-09
---

## paths

src/ContentPilot.Renderer/Video/
src/ContentPilot.Renderer/Templates/Reel/
tests/ContentPilot.RendererTests/Video/

## notes

Phase 7 — reels. Read IMPLEMENTATION-PLAN.md §17 before starting.

**Do not use Remotion.** Render PNG layers through the existing Playwright pipeline, then
compose them with a single FFmpeg filtergraph. That reuses the whole template and QA stack,
needs no Node runtime, and has no licence question. FFmpeg is already in
`docker/Dockerfile.renderer`.

New reel DTOs go in a **new file** in `ContentPilot.Rendering.Contracts` — that project is
append-only while two agents are working. Never modify an existing record or enum there;
Phase 2's renderer, its 20 tests and the calibrated fidelity thresholds depend on the
current shapes.

`migrations: false` — this phase should need no schema change. If it does, write the
entity and its configuration, then stop and note it here.
