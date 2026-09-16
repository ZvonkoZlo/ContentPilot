---
agent: codex
phase: screenshot-fidelity
branch: main
status: done
migrations: false
updated: 2026-09-16
---

## paths

src/ContentPilot.Renderer/Engine/
src/ContentPilot.Renderer/Imaging/
src/ContentPilot.Renderer/Templates/Static/
tests/ContentPilot.RendererTests/

## notes

Investigate and fix the live `ScreenshotWrongAsset` failure without changing the calibrated
threshold first. Scope is the renderer's screenshot compositing/extraction path and focused
renderer regression coverage. Application quality checks, template selection, orchestration
and `frontend/` remain under Claude's active claim and are read-only for this work.

Merged as `635e8cc` on 2026-09-16. Live Linux replay changed the exact failing
screenshot from `ScreenshotWrongAsset` (pHash 17) to Pass while retaining the pHash ceiling
of 12; SSIM 0.9955 independently confirmed the asset. See `PARALLEL-WORK.md` for validation.
