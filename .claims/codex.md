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
tests/ContentPilot.RendererTests/
TODO-NEXT.md
PARALLEL-WORK.md

## notes

Fix immutable ProductScreenshot slots that stretch mismatched source aspect ratios in
PhoneFloating and FeatureHighlight. Verify crop/occlusion behavior and add a renderer fidelity
regression using a deliberately mismatched screenshot aspect ratio.
