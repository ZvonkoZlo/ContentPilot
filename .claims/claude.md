---
agent: claude
phase: 8
branch: main
status: active
migrations: true
updated: 2026-09-11
---

## paths

src/ContentPilot.Domain/Content/
src/ContentPilot.Domain/Observability/
src/ContentPilot.Domain/Quality/
src/ContentPilot.Domain/Workflow/
src/ContentPilot.Domain/Packaging/
src/ContentPilot.Application/Ai/
src/ContentPilot.Application/Agents/
src/ContentPilot.Application/Prompts/
src/ContentPilot.Application/ContentMemory/
src/ContentPilot.Application/Quality/
src/ContentPilot.Application/Orchestration/
src/ContentPilot.Application/Jobs/
src/ContentPilot.Application/Campaigns/
src/ContentPilot.Application/Packaging/
src/ContentPilot.Infrastructure/Ai/
src/ContentPilot.Infrastructure/Branding/
src/ContentPilot.Infrastructure/Campaigns/
src/ContentPilot.Infrastructure/Jobs/
src/ContentPilot.Infrastructure/Rendering/
src/ContentPilot.Infrastructure/Packaging/
src/ContentPilot.Infrastructure/Persistence/Migrations/
src/ContentPilot.Api/Endpoints/CampaignEndpoints.cs
src/ContentPilot.Worker/Program.cs
tests/ContentPilot.UnitTests/Agents/
tests/ContentPilot.UnitTests/Ai/
tests/ContentPilot.UnitTests/Quality/
tests/ContentPilot.UnitTests/Orchestration/
tests/ContentPilot.UnitTests/Domain/
tests/ContentPilot.UnitTests/Rendering/
tests/ContentPilot.UnitTests/Campaigns/
tests/ContentPilot.UnitTests/Packaging/
tests/ContentPilot.IntegrationTests/AssetContentResolverTests.cs
tests/ContentPilot.IntegrationTests/ContentItemWorkflowJobHandlerTests.cs
tests/ContentPilot.IntegrationTests/CampaignWorkflowJobHandlerTests.cs
tests/ContentPilot.IntegrationTests/CampaignTriggerJobHandlerTests.cs
tests/ContentPilot.IntegrationTests/CampaignPackagerTests.cs
tests/ContentPilot.WorkflowTests/

## notes

**Phase 5 done** — see PARALLEL-WORK.md for the full history.

**Phase 6 — Visual QA and Marketing QA — core is done.** `VisualQaAgent` (gate 2) and
`MarketingQaAgent` (gate 3) are both built, wired into `ContentItemWorkflowJobHandler`
between Rendering and Validating, and covered by unit + integration tests. See
PARALLEL-WORK.md for the full history of this phase.

Landed this pass, on top of the vision-support foundation (`LlmRequest.Images`,
`IAgent<,>.BuildImages`, both provider adapters):

- `VisualQaAgent` / `VisualQaOutput` / `VisualQaValidator` / `visual-qa.prompt.md` — judges
  the full render plus a 150px thumbnail (`ImageThumbnailer`, Infrastructure/Quality/)
  against the 6xx band.
- `MarketingQaAgent` / `MarketingQaOutput` / `MarketingQaValidator` / `marketing-qa.prompt.md`
  — text-only, judges copy against the 7xx band; its main job is claim-grounding (does the
  copy's claim match what the cited `ProductFact` actually says, not just that a citation key
  exists).
- `"visual-qa"` profile added to both `appsettings.json` files; both agents registered in
  `AiServiceCollectionExtensions`.
- `RemediationRouter.MinConfidenceForRemediation = 0.6` + `PrimaryFinding` filtering — §9's
  "low-confidence findings are recorded but do not trigger remediation" rule. Deterministic
  findings have no `Confidence` and are always acted on; model findings below the threshold
  are persisted in the `QualityReview` row but excluded from the set `OrchestratorCore.Decide`
  sees.
- `ContentItemWorkflowJobHandler.ExecuteRenderingAsync`: gate 1 runs first; gates 2 and 3 run
  only if gate 1 didn't already fail (avoids paying for a model judgement on a render already
  known bad). Gates 2 and 3 run **sequentially, not via `Task.WhenAll`** — `AppDbContext` is
  not safe for concurrent operations on one instance; this bit us once (test failure), fixed
  by sequencing rather than adding a second DbContext scope. `FinishAttemptAsync` now takes
  `IReadOnlyList<QaReport>` and writes one `QualityReview` row per gate.

**Still not done for phase 6** (deliberately deferred, not forgotten):

- The QA pass-rate metric §9 calls for (>60% first-attempt pass, false-positive rate over
  0.15 = blocking regression — §27's own numbers). No metric/dashboard exists yet; would need
  real (non-scripted) model runs to measure honestly.
- §27's eval scenarios (VisualQA catches mutated screenshots, false-positive rate ceiling)
  are not built — they need golden fixture images, which don't exist yet.
- Carousel continuity checks (`CarouselDiscontinuity` code) are moot until Phase 5's item
  handler drives carousels — it currently only drives `StaticPost`.

`migrations: false` — phase 6 added no tables (`QualityReview` and `QaFinding` already
carry everything a model-gate finding needs).

## Phase 8 — packaging, delivery, human review

**Core landed.** `CampaignPackager` builds `plan.json`/`manifest.json` and every item's
`post-NN/image.png|caption.txt|metadata.json` under `campaigns/{campaignId}/...`, wired into
`CampaignWorkflowJobHandler.CheckCompletionAsync` in place of the old placeholder. Migration
`CampaignPackaging` (`campaign_packages`, `human_ratings`) applied. Browse/rating API added
to `CampaignEndpoints.cs`: `GET /{id}/package`, `POST /{id}/items/{itemId}/rating`. Along the
way, found and fixed a real gap: rendered image bytes were never actually uploaded to object
storage before this (`ContentAsset.StorageKey` held a `pending/...` placeholder) — now
uploaded to `runs/{itemId}/attempts/{attempt}/{sha256}.ext` in
`ContentItemWorkflowJobHandler`. See PARALLEL-WORK.md's Phase 8 section for the full history,
including the Docker Desktop API-version workaround for running the Docker suites here
(`DOCKER_API_VERSION=1.43 dotnet test ...`).

**ZIP landed too.** `CampaignZipBuilder` (`Infrastructure/Packaging/`) builds
`campaigns/{campaignId}/package.zip` on demand from the manifest's file list (plus
`plan.json`/`manifest.json`), streamed through a temp file rather than buffered in RAM, and
cached on `CampaignPackage.ZipKey` — `Rebuild()` already clears it, so a stale ZIP is never
served and an unchanged package never rebuilds one. `POST /api/campaigns/{id}/download`
returns a 15-minute presigned URL, building the ZIP the first time it's asked for.

Items needing review are no longer hidden from the package either: `NeedsHumanReview` items
with a promoted best attempt package into `_needs-review/item-NN/` (separate numbering from
`post-NN`) with their QA findings and failure reason in `metadata.json`. Numbering is also
now stable per §13 (publish day then creation order).

**Not yet built:** the review UI (no frontend exists at all yet), the weekly email,
retention/tenant-deletion jobs.
