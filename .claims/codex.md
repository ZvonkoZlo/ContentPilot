---
agent: codex
phase: screenshot-slot-crop
branch: fix-screenshot-slot-crop
status: done
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

Merged to `main` through `bd1e5cd` on 2026-09-17. The proposed `cover` crop was measured and
rejected because it dropped clean fidelity to SSIM 0.653. The actual narrow-slot resampling
false positive is fixed in `FidelityComparer`; all 37 renderer tests and the 140-case
calibration corpus pass, with one existing FFmpeg-path skip.
