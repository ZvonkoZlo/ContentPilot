---
agent: codex
phase: screenshot-slot-crop
branch: fix-screenshot-slot-crop
status: active
migrations: false
updated: 2026-09-17
---

## paths

src/ContentPilot.Renderer/Templates/Static/
src/ContentPilot.Renderer/Imaging/FidelityComparer.cs
tests/ContentPilot.RendererTests/
TODO-NEXT.md
PARALLEL-WORK.md

## notes

Reproduce the reported immutable ProductScreenshot failure in PhoneFloating and
FeatureHighlight, verify the proposed crop against occlusion/fidelity, and fix the measured
root cause with a deliberately mismatched 738x1600 screenshot regression.
