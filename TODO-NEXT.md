# TODO-NEXT.md — handover for the next agent (Codex)

Written 2026-09-16 by Claude, after the first fully live end-to-end run against the real
Anthropic API (see `PARALLEL-WORK.md` for the six infra bugs found and fixed that same
session — all committed and pushed to `main` on
[github.com/ZvonkoZlo/ContentPilot](https://github.com/ZvonkoZlo/ContentPilot)).

Read `CLAUDE.md` → `PARALLEL-WORK.md` and claim your paths in `.claims/` before touching
anything, same as always. This file is a punch list, not a replacement for those.

## 1. The one real product bug still open: screenshot slot never passes QA

Every live campaign so far has every `StaticPost` item end at `NeedsHumanReview` with the
exact same finding:

```
ScreenshotWrongAsset at Validating: quality attempts exhausted (3/3).
"The image in 'screenshot' is not the source asset (perceptual distance 17)."
```
(threshold is 12 — see `src/ContentPilot.Application/Quality/Checks/FidelityChecks.cs:80-93`)

This is not an infra bug — the pipeline runs cleanly end to end (render, QA, remediation
loop all execute correctly). It's a real defect somewhere in the screenshot path, and it
blocks 100% of `StaticPost` items from ever reaching `Approved`.

**Where to look:**
- `src/ContentPilot.Application/Quality/Checks/FidelityChecks.cs` — the check itself
  (perceptual-hash distance between the rendered slot and the source asset it was supposed
  to be).
- `src/ContentPilot.Application/Agents/TemplateSelector.cs` and whatever assembles the
  `CreativeSpec` for the `SpecAssembly` step — confirm the spec actually references the
  *asset the strategist/director intended*, not a stale or wrong `BrandAsset.Id`.
- The renderer's compositing of a `ProductScreenshot` slot
  (`src/ContentPilot.Renderer/` — look for how a screenshot slot is cropped/scaled into the
  template) — if the renderer changes the image enough (aggressive crop, letterboxing,
  color-profile conversion) the perceptual hash could legitimately drift past the
  threshold even for the *correct* asset.
- Worth first reproducing by hand: pull one item's `CreativeSpec` (`workflow_steps.result_json`
  for the `SpecAssembly` step) and the actual rendered image, and compare which
  `BrandAsset.Id` was requested vs. what shows up in the final render.

**Suggested fix path:** find the actual root cause first — don't just raise the threshold.
If the asset reference is right and the renderer is legitimately transforming the image,
the fidelity check may need to compare against the *post-crop* reference region, not the
whole source asset.

## 2. Brand Brain UI gaps — backend already done, no frontend

The new `/brand` page (`frontend/src/app/brand-profile/`) covers visual identity, voice,
messaging, and the asset library. It does **not** cover:

- **Personas** — `GET/POST /api/brands/{id}/personas` (`BrandBrainEndpoints.cs`). No UI.
- **Product facts** — `GET/POST/DELETE /api/brands/{id}/facts`. No UI. These are what the
  copy validator checks claims against, so this one matters for content quality, not just
  convenience.
- **Content preferences** (weekly quota, excluded/preferred topics, generation schedule) —
  `GET/PUT /api/brands/{id}/preferences`. No UI.

Same pattern as the Brand page: read `frontend/src/app/brand-profile/brand-profile.component.ts`
for the conventions (signals, `ApiService` wrapper, comma-separated-text-to-array helper)
and either extend that component or add sibling ones + a route.

## 3. Review UI — smaller gaps

- **Item detail has no image preview.** `campaign-detail.component.html` lets you
  Approve/Reject/Rate/Download but never shows the actual rendered image — you have to
  download it to see what was produced. Add an `<img>` using the presigned URL (there's
  already a pattern for this in `AssetEndpoints`'s `/url` route — check whether an
  equivalent exists for a rendered `ContentItem`'s output, or add one).
- **Dashboard has no cost/QA-pass-rate summary** — `getCost`/`getQaPassRate` are already in
  `ApiService` and used inside campaign-detail, but the campaign list on the dashboard
  doesn't surface either at a glance.
- This whole `frontend/` app is explicitly **not** the Phase 8 review UI from
  `IMPLEMENTATION-PLAN.md` — no auth, no real design system, functional only. Worth keeping
  in mind before investing in polish here vs. treating it as a permanent internal tool.

## 4. Phase 6 — Visual/Marketing QA (in progress, per AGENT-MAP.md)

- §27 eval scenarios for Visual QA / Marketing QA agents — not built yet.
- Carousel continuity checks (cross-slide consistency for carousel-type items) — not built.

## 5. Phase 8 — Packaging/delivery

- The weekly email (campaign package notification) is the one piece of Phase 8's backend
  still missing. Packaging, ZIP, browse/download/approve/reject/rating/findings/run-tree/
  cost/retention are all done.

## 6. Phase 9 — Hardening, cost calibration, evals

- Eval catalogue beyond the 3 existing deterministic scenarios.
- An actual cost/quality dashboard (admin views exist, this is more than that).
- "Live mode" calibration — needs real spend/data, which this session's live runs are a
  first data point for but not enough to calibrate against.

## Not a task, just context worth knowing

Every infra bug found this session (6 of them, all fixed and committed — see `git log` and
`PARALLEL-WORK.md`) was found **only** by running the system live against the real
Anthropic API. The 500+ existing automated tests structurally can't catch this class of bug
— they use scripted/cassette models that bypass the real decorator chain, real multi-second
responses, real multi-job remediation restarts, and real renderer HTTP calls. If you're
debugging something that "should" work per the tests but doesn't in practice, a live run
plus direct SQL against `workflow_steps`/`jobs` (see commit `f6fadc0`'s message for the
technique) is usually faster than trusting the test suite's silence.
