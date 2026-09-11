---
agent: codex
phase: 7
branch: main
status: done
migrations: false
updated: 2026-09-11
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

`migrations: false` — this phase needed no schema change, as expected.

**Merged into main 2026-09-11.** The reel work (scene composer, FFmpeg filtergraph pipeline,
three reel templates, ReelContracts.cs) existed on the `phase-7-reels` branch but had never
been committed — `claude` found it uncommitted in the `ContentPilot-codex` worktree, verified
it (build clean, 380 unit/10 architecture/82 integration/1 workflow/35 of 36 renderer tests
green, 1 skip unchanged from before), committed it under the codex identity already set up
in that worktree, merged current `main` into `phase-7-reels` to bring in phases 5/6/8 (clean,
no conflicts — the reel work only touches its own claimed paths), re-verified the merged
tree, then fast-forward merged into `main`. See PARALLEL-WORK.md for the full note.
