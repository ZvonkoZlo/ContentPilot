# TODO-NEXT.md — handover for the next agent (Codex)

Written 2026-09-16 by Claude, after the first fully live end-to-end run against the real
Anthropic API (see `PARALLEL-WORK.md` for the six infra bugs found and fixed that same
session — all committed and pushed to `main` on
[github.com/ZvonkoZlo/ContentPilot](https://github.com/ZvonkoZlo/ContentPilot)).

Read `CLAUDE.md` → `PARALLEL-WORK.md` and claim your paths in `.claims/` before touching
anything, same as always. This file is a punch list, not a replacement for those.

## 1. Fixed 2026-09-16: screenshot slot pHash false positive

Every live campaign so far has every `StaticPost` item end at `NeedsHumanReview` with the
exact same finding:

```
ScreenshotWrongAsset at Validating: quality attempts exhausted (3/3).
"The image in 'screenshot' is not the source asset (perceptual distance 17)."
```
(threshold is 12 — see `src/ContentPilot.Application/Quality/Checks/FidelityChecks.cs:80-93`)

**Resolved by Codex on `fix-screenshot-wrong-asset`.** The selected `BrandAsset.Id`,
CreativeSpec payload and rendered pixels were all correct. The live sparse UI screenshot
measured pHash distance 17 on Linux, but SSIM 0.9955, mean DeltaE 0.05, aspect drift 0.16%
and zero occlusion. DCT median bits are unstable for sparse, low-frequency screens across
browser/ImageMagick resampling. `FidelityComparer` now treats pHash as a candidate signal
and confirms a different asset with the structural failure floor before returning
`ScreenshotWrongAsset`; the threshold remains 12. A deterministic sparse-UI regression
fixture fails on the old code and passes with the fix, while the existing wrong-image and
mutation corpus still fails where expected.
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

## 2. Fixed 2026-09-17: Brand Brain UI gaps

The `/brand` page now covers visual identity, voice, messaging, the asset library, and:

- **Personas** — list/add, including pains, goals, objections, vocabulary and primary status.
- **Product facts** — list/add/delete, including evidence, visibility and validity window.
- **Content preferences** — quota, preferred/excluded topics, publish days and generation schedule.

## 3. Fixed 2026-09-17: Review UI gaps

- **Item detail image preview** uses a tenant/campaign-scoped presigned URL for the promoted
  or latest render; the endpoint refuses an item from another campaign.
- **Campaign dashboard cost/QA summary** shows total cost and first-attempt pass rate inline
  for every listed campaign.
- This whole `frontend/` app is explicitly **not** the Phase 8 review UI from
  `IMPLEMENTATION-PLAN.md` — no auth, no real design system, functional only. Worth keeping
  in mind before investing in polish here vs. treating it as a permanent internal tool.

## 4. Phase 6 — Visual/Marketing QA (in progress, per AGENT-MAP.md)

- §27 eval scenarios for Visual QA / Marketing QA agents — not built yet.
- Carousel continuity checks (cross-slide consistency for carousel-type items) — not built.

The buildable deterministic §27 row "CreativeDirector picks a legal template and asset" is
now the fourth persisted eval scenario. VisualQA recall/false-positive rows still need
hand-labelled golden renders and recorded/live model judgements; none exist in the repo yet.
Carousel continuity still has no pipeline execution path because the item workflow currently
drives only `StaticPost`, despite the Phase 7 renderer primitives being present.

## 5. Fixed 2026-09-17: Phase 8 weekly campaign email

Phase 8's remaining backend delivery piece is implemented. Each `Brand` has its own optional
`NotificationEmail`, managed through `PUT /api/brands/{id}/notification-email`; the new
nullable column is delivered by the `BrandNotificationEmail` migration. Completion sends a
short plain-text summary with Approved/NeedsHumanReview counts and a durable link to
`/campaign/{id}`. A short-lived object-storage URL is deliberately not embedded.

`IEmailSender` is implemented with MailKit over STARTTLS. Configuration follows the existing
secret pattern (`Email__Enabled`, `Email__Smtp__Host/Port/Username/Password`,
`Email__ReviewUiBaseUrl`), is disabled by default, and docker compose explicitly forwards the
variables. Credentials stay in `.env`/the deployment secret store. `EmailSentAt` is marked
only after a successful send; a transport failure is logged and does not fail the campaign.
Integration coverage proves successful once-only delivery, failure isolation, recipient
validation and migration application. No live email was sent during automated validation.

## 6. Phase 9 — Hardening, cost calibration, evals

- Eval catalogue beyond the 3 existing deterministic scenarios.
- An actual cost/quality dashboard (admin views exist, this is more than that).
- "Live mode" calibration — needs real spend/data, which this session's live runs are a
  first data point for but not enough to calibrate against.

The minimal UI now has an `/admin` operator page for dead jobs, stuck runs and persisted eval
trends with per-scenario detail. Campaign rows also surface their cost and first-pass QA rate.
Real threshold/budget calibration still needs the planned ten live campaigns; code cannot
manufacture that dataset honestly.

No weekly aggregate endpoint was added yet. The existing per-campaign cost/QA summaries and
persisted eval trend endpoint already capture the data needed for the first calibration pass;
the useful weekly grouping and filters should be chosen from observed usage rather than added
speculatively before the ten-campaign dataset exists.

## Not a task, just context worth knowing

Every infra bug found this session (6 of them, all fixed and committed — see `git log` and
`PARALLEL-WORK.md`) was found **only** by running the system live against the real
Anthropic API. The 500+ existing automated tests structurally can't catch this class of bug
— they use scripted/cassette models that bypass the real decorator chain, real multi-second
responses, real multi-job remediation restarts, and real renderer HTTP calls. If you're
debugging something that "should" work per the tests but doesn't in practice, a live run
plus direct SQL against `workflow_steps`/`jobs` (see commit `f6fadc0`'s message for the
technique) is usually faster than trusting the test suite's silence.
