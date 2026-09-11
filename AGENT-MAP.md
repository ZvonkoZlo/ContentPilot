# AGENT-MAP.md — the index, so you do not read the whole repo

Written for agents, not humans. Purpose: answer "where does this live?" and "which lines of
the plan do I need?" **without** grepping the tree or pulling 113 KB of plan into context.
`IMPLEMENTATION-PLAN.md` alone is ~30k tokens — read it by line range, never whole.

The contract and the rules stay in [CLAUDE.md](CLAUDE.md) / [AGENTS.md](AGENTS.md) →
[PARALLEL-WORK.md](PARALLEL-WORK.md). This file is the map only.

## How to read the plan cheaply

```bash
sed -n '1227,1247p' IMPLEMENTATION-PLAN.md    # exactly one phase
sed -n '739,766p'   IMPLEMENTATION-PLAN.md    # the template manifest
```

**Sections** — `§n` → start line; read to the next section's line minus one. File ends at 1503.

| § | Topic | Line | § | Topic | Line |
|---|---|---|---|---|---|
| 1 | Executive summary | 16 | 20 | Background jobs | 892 |
| 2 | Architecture in text | 35 | 21 | Triggers | 922 |
| 3 | Decisions and why | 136 | 22 | Output and packaging | 953 |
| 4 | MCP — where yes | 154 | 23 | Observability | 984 |
| 5 | Agent architecture | 176 | 24 | Cost tracking and limits | 1028 |
| 6 | Orchestrator design | 242 | 25 | Security | 1064 |
| 7 | Workflow state machine | 290 | 26 | Testing strategy | 1082 |
| 8 | Retry and self-correction | 314 | 27 | AI eval strategy | 1099 |
| 9 | QA architecture | 358 | 28 | Docker and deployment | 1128 |
| 10 | Screenshot validation | 385 | 29 | Config and secrets | 1152 |
| 11 | Solution structure | 450 | 30–31 | **MVP phases** | 1162 |
| 12 | Domain entities | 527 | 32 | Not to build yet | 1373 |
| 13 | Database and storage | 581 | 33 | Technical risks | 1391 |
| 14 | Brand Brain | 629 | 34 | Decisions before code | 1408 |
| 15 | Content memory | 662 | 35 | Proof of concept | 1424 |
| 16 | Static image rendering | 696 | 36–37 | Complexity and order | 1438 |
| 17 | Reels | 773 | 38 | Closing | 1459 |
| 18 | AI provider abstraction | 815 | | | |
| 19 | Prompt management | 857 | | | |

**Phases** — about 20 lines each, start line given.

| Phase | Line | Status |
|---|---|---|
| 0 — Foundations, walking skeleton | 1166 | done |
| 1 — Brand Brain and asset library | 1186 | done |
| 2 — Renderer and static templates | 1206 | done |
| 3 — Text agents and the LLM layer | 1227 | done |
| 4 — Deterministic QA and fidelity calibration | 1248 | done (`claude`) |
| 5 — Orchestrator, retries, self-correction | 1266 | done (`claude`) — manual/scheduled/reconciled trigger → campaign → items → Approved for StaticPost; only image generation out of scope |
| 6 — Visual QA and Marketing QA | 1285 | in progress (`claude`) — `VisualQaAgent`/`MarketingQaAgent` built and wired into the live loop; still open: QA pass-rate metric, §27 evals, carousel continuity |
| 7 — Reels | 1303 | in progress (`codex`, branch `phase-7-reels`) |
| 8 — Packaging, delivery, human review | 1322 | unclaimed |
| 9 — Hardening, cost calibration, evals | 1342 | unclaimed |
| 10 — Post-MVP options | 1359 | not started |

Sub-sections worth jumping straight to: agent contract 208, validators 229, orchestrator
core loop 260, escalation ladder 330, QA gate 1 362, mask-render 395, scale-normalised
comparison 408, architecture tests 517, Postgres conventions 583, object storage layout 606,
novelty validator 670, determinism rules 731, **template manifest 739**, reels approach 777,
cost enforcement 1034, golden test data 1093.

## The seven projects

| Project | Holds | Depends on |
|---|---|---|
| `ContentPilot.Domain` | entities, invariants, `Entity` / `ITenantOwned` / `IAuditable` / `IAppendOnly`, `Guard`, `NewId` | nothing |
| `ContentPilot.Application` | ports (`I*`), agents, prompts, brand assembly, content memory | Domain, Rendering.Contracts |
| `ContentPilot.Infrastructure` | EF/Postgres, job queue, S3, LLM adapters, telemetry | Domain, Application |
| `ContentPilot.Api` | minimal-API endpoints, tenant middleware | the above |
| `ContentPilot.Worker` | job host | Infrastructure |
| `ContentPilot.Renderer` | separate service: Playwright, templates, fidelity/mask/contrast imaging | Rendering.Contracts only |
| `ContentPilot.Rendering.Contracts` | the wire types between them — **append-only** | nothing |

Layering is enforced by `tests/ContentPilot.ArchitectureTests/LayeringRules.cs`, tenancy by
`TenantIsolationRules.cs`. A violation fails the build, so do not "just add a reference".

## Where things are

**Ports** — `Application/Abstractions/`: `IClock`, `IJobQueue` with `IJobHandler` /
`JobHandler<T>` / `JobTypeAttribute` / `JobPriority` / `PermanentJobFailureException`,
`IObjectStore` with `ObjectKey`, `ITenantContext` / `IMutableTenantContext`, `IUnitOfWork`,
`IImageIngestor`.

**AI** — `Application/Ai/`: `ILanguageModelClient`, `LlmRequest` / `LlmResponse` /
`TokenUsage`, `ModelProfile` / `ModelProvider` / `ModelEffort` / `IModelProfileRegistry`.
`Infrastructure/Ai/` decorates one client in a chain: `ProviderRoutingLanguageModelClient` →
`AnthropicLanguageModelClient` / `OpenAiLanguageModelClient`, wrapped by `Budgeted-`,
`Resilient-`, `Cassette-`. Wiring is `AiServiceCollectionExtensions.AddContentPilotAi`.
Config shapes live in `AiOptions.cs` (`AiOptions`, `ProviderOptions`, `ModelProfileOptions`,
`CassetteMode`); keys are environment-only (`Ai__Providers__<Vendor>__ApiKey`) and
`Ai:Enabled` is false by default, so nothing can spend until someone opts in.

**Agents** — `Application/Agents/`: `IAgent<TInput,TOutput>`, `AgentResult<T>`,
`AgentValidationException`; `ContentStrategistAgent` with `StrategistInput` and
`RecentContent`; `WeeklyPlan` / `PlannedItem` / `WeeklyPlanSchema`; `PlanValidator`;
`CopywriterAgent` with `CopywriterInput`; `CopySet` / `CopySlot` / `CopySetSchema`;
`CopyValidator` with `CopySlotBrief`; `TemplateSelector` (template eligibility computed from
manifests); `SpecAssembler` (template + `CopySet` + `BrandSnapshot` → `RenderImageRequest`,
plus `ComputeHash` and the `VisualIdentity` → `BrandTokens` mapping — SpecAssembly's
executor, deterministic like `TemplateSelector`); `VisualQaAgent` (gate 2, vision — full
image + 150px thumbnail) with `VisualQaInput`/`VisualQaOutput`/`VisualQaFinding`,
`VisualQaValidator`; `MarketingQaAgent` (gate 3, text-only — claim-grounding is its main
job) with `MarketingQaInput`/`MarketingQaOutput`/`MarketingQaFinding`, `MarketingQaValidator`.
Both validators reject an unknown code, a code from the wrong `QaGate` band
(`QaFindingCodes.BelongsTo`), out-of-range confidence, or a blank detail. Execution and run
persistence: `Infrastructure/Ai/AgentExecutor.cs` with
`AgentContext` (computes `agent.BuildImages(input)` once per call — see `IAgent<,>` below).
Prompts: `Application/Prompts/` (`PromptLibrary`, `PromptTemplate`,
`content-strategist.prompt.md`, `copywriter.prompt.md`, `visual-qa.prompt.md`,
`marketing-qa.prompt.md`), registry in `Infrastructure/Ai/PromptRegistry.cs`.
`IAgent<TInput,TOutput>.BuildImages` is a default interface member (returns empty) — call it
through the interface type, not the concrete class, or it won't resolve.
`ILanguageModelClient`'s `LlmRequest.Images` (`IReadOnlyList<LlmImageAttachment>`) is what
carries them to the provider; both Anthropic and OpenAI adapters send images first, text
last. `Infrastructure/Quality/ImageThumbnailer.cs` (Magick.NET) makes the 150px thumbnail
VisualQA needs.

**Content memory** — `Application/ContentMemory/SimHash.cs`;
`Infrastructure/Branding/ContentMemoryReader.cs`.

**Quality (gates 1–3)** — `Domain/Quality/` (`QaFinding` — has an optional `Confidence`, null
for deterministic findings, set by model gates; `QaFindingCode` — append-only and grouped by
hundreds: 1xx layout/2xx logo/3xx fidelity/4xx file sanity/5xx video are gate 1, 6xx visual
judgement is gate 2, 7xx marketing judgement is gate 3; `QaFindingCodes.GateFor`/`BelongsTo`
—, `QaSeverity`, `QaGate`, `QaOutcome`, `QualityReview` — one row per gate per attempt).
`Application/Quality/` (`DeterministicQaSuite` — gate 1, `DeterministicQaInput`/
`DeterministicQaOptions`, `QaReport`, `Checks/` — `LayoutChecks`, `ContrastCheck`,
`LogoChecks`, `FidelityChecks`, `FileSanityChecks`). Gates 2/3 are `VisualQaAgent`/
`MarketingQaAgent` under Agents above, not here — they're agents, not pure checks. EF
configuration in `Infrastructure/Persistence/Configurations/
QualityConfigurations.cs`. Calibration harness and threshold rationale:
`tests/ContentPilot.RendererTests/FidelityCalibrationTests.cs`, regenerating
`artifacts/fidelity-calibration.md` (git-ignored) on every run.

**Orchestration** — `Application/Orchestration/` (`ItemStateMachine` — legal §7 item
transitions and loop-safety; `RemediationRouter` — §8 finding-code-to-restart-step table
plus the escalation ladder, plus `MinConfidenceForRemediation = 0.6` — §9's rule that a
low-confidence model-gate finding is recorded but never drives a remediation decision (only
`PrimaryFinding`'s input is filtered by this; the `QualityReview` row keeps every finding
unfiltered); `BudgetGuard` — §24 reserve-then-commit, wired into
`ContentItemWorkflowJobHandler`; `OrchestratorCore.Decide` — the `(WorkflowRun, WorkflowStep[]) => NextAction`
function §6 names directly). `Domain/Workflow/` (`WorkflowRun` — attempt counters and
deadline read from `Domain.Tenancy.TenantLimits`, `WorkflowStep` — append-only, idempotency
key `(RunId, StepName, Attempt)`, plus `ResultJson` for carrying one step's decision to the
next; `BudgetReservation`); `ContentRevision` lives in `Domain/Content/` next to
`ContentItem`. EF configuration in `Infrastructure/Persistence/Configurations/
WorkflowConfigurations.cs`. The renderer client — `Application/Abstractions/
IRendererClient.cs`, implemented by `Infrastructure/Rendering/HttpRendererClient.cs` — gives
Rendering and Validating a real way to reach the Renderer service, alongside
`TemplateSelector` for Directing, `SpecAssembler` for SpecAssembly, and
`DeterministicQaSuite` for Validating's deterministic half.

**The callers** — `Infrastructure/Jobs/ContentItemWorkflowJobHandler.cs` (job type
`advance-content-item-workflow`, payload `Application/Jobs/
AdvanceContentItemWorkflowPayload.cs`) drives a `StaticPost` item's `WorkflowRun` end to
end: Directing → Writing → SpecAssembly → AssetGeneration → Rendering → Validating,
looping through as many steps as it can in one job invocation, §24 budget-checked before
Writing, promoting the best attempt on `NeedsHumanReview`, proven crash-resumable.
`Infrastructure/Jobs/CampaignWorkflowJobHandler.cs` (job type `advance-campaign-workflow`,
payload `AdvanceCampaignWorkflowPayload`) plans a campaign with `ContentStrategistAgent`,
creates each planned item plus its own `WorkflowRun`, enqueues the item job for each, and
closes the campaign out via `ContentCampaign`'s own transition methods once every item is
terminal. `Infrastructure/Campaigns/CampaignStarter.cs` is §21's single implementation
behind every way a campaign starts; `Application/Campaigns/CampaignWeek.cs` is the shared
"which Monday" arithmetic. `Infrastructure/Jobs/CampaignTriggerScanJobHandler.cs` (hourly,
per-brand-timezone 06:00-Monday check) and `CampaignTriggerReconcileJobHandler.cs` (daily
safety net) are §21's scheduled and reconciled triggers — both self-rescheduling jobs on the
existing queue, a deliberate substitution for the plan's named Hangfire (see
PARALLEL-WORK.md for the reasoning); `Worker/Program.cs` seeds the first occurrence of each
idempotently on startup. `Api/Endpoints/CampaignEndpoints.cs` — `POST /api/campaigns`,
`GET /api/campaigns/{id}`, `GET /api/campaigns/{id}/items`, `POST
/api/campaigns/{id}/cancel` — is the manual trigger from §21, its read side, and
cancellation (campaign-level only; items already in flight are not reached). Proven end to
end against real Postgres/MinIO in `ContentItemWorkflowJobHandlerTests.cs`,
`CampaignWorkflowJobHandlerTests.cs`, `CampaignTriggerJobHandlerTests.cs`, and the campaign
tests appended to `ApiEndpointTests.cs`. **Phase 5 is done except background image
generation** (no provider chosen, no client exists — degrades to fewer eligible templates,
never a stuck item) — carousel/reel composition and real packaging are Phase 6/7/8, never
part of this phase's own scope. See PARALLEL-WORK.md's phase 5 sections for the full history.

**Brand Brain** — `Application/Brand/` (`BrandBrainAssembler`, `BrandSnapshot` and its views,
`BrandBlockRenderer`); port `Application/Capabilities/IBrandBrainReader.cs`; implementation
`Infrastructure/Branding/BrandBrainReader.cs`; seed `GoldenTenantSeeder.cs`; assets
`AssetLibrary.cs` and `Infrastructure/Assets/` (`ImageIngestor`, `SvgSanitizer`). Asset
*bytes* for the render pipeline (as opposed to the metadata `BrandSnapshot` carries): port
`Application/Abstractions/IAssetContentResolver.cs`, implementation
`Infrastructure/Branding/AssetContentResolver.cs`.

**Entities** — `Domain/Tenancy/` (`Tenant`, `TenantLimits` — every attempt/cost/deadline
ceiling in the system, tuned per tenant, never hard-coded); `Domain/Branding/` (`Brand`,
`BrandProfile` with `ToneOfVoice` / `VisualIdentity` / `Messaging`, `BrandProfileVersion`,
`BrandAsset`, `ProductFact`, `AudiencePersona`, `ContentPreferences`, `IndustryProfile`);
`Domain/Content/` (`ContentCampaign`, `ContentItem`, `ContentHistoryEntry`, `ContentRevision`
— the attempt journal, `TemplateVersion` — a pinned manifest snapshot, `CreativeSpec` — the
immutable per-attempt render input, `ContentAsset` — every attempt's rendered output);
`Domain/Observability/` (`AgentRun`, `CostEntry`, `PromptVersion`); `Domain/Quality/`
(`QualityReview`, see above); `Domain/Workflow/` (see Orchestration, above). EF
configurations mirror those under `Infrastructure/Persistence/Configurations/`
(`CreativeConfigurations.cs` for the three new ones).

**Rendering contracts** — `TemplateManifest.cs` (`TemplateManifest`, `TextSlot`, `AssetSlot`,
`SafeAreas`, `AspectRatio`, `ColorScheme`, `TemplateContentType`); `RenderContracts.cs`
(`RenderImageRequest` / `RenderImageResponse`, `RenderReport`, `SlotMeasurement`,
`BoundingBox`, `BrandTokens`, `ImagePayload`, `RenderOptions`); `CompareContracts.cs`
(`CompareRequest` / `CompareResult`, `FidelityMetrics`, `FidelityThresholds`,
`FidelityVerdict`). **Never edit these — new types go in new files.**

**Renderer** — `Engine/` (`BrowserPool`, `DocumentBuilder`, `FontLibrary`,
`ImageRenderService`, `TemplateCatalog`, `RenderScripts`); `Imaging/` (`FidelityComparer`,
`PerceptualHash`, `ColorDifference`, `ImageMetrics` → `MaskAnalyzer`, `ContrastAnalyzer`);
`Templates/Static/`; endpoints in `Endpoints/RenderEndpoints.cs`.

**Jobs** — `Infrastructure/Jobs/`: `PostgresJobQueue` with `JobQueueOptions`, `Job` /
`JobState`, `JobDispatcher`, `JobReaper`. Enqueue enlists in the caller's transaction; it
does not save.

**API endpoints** — `Api/Endpoints/`: `TenantEndpoints`, `BrandEndpoints`,
`BrandBrainEndpoints`, `AssetEndpoints`, `DiagnosticsEndpoints`, `CampaignEndpoints` (the
manual "generate now" trigger — see Orchestration above).

## Tests — five suites, know which one you need

| Suite | Needs | What it covers |
|---|---|---|
| `ContentPilot.UnitTests` | nothing | domain, agents, prompts, AI routing and cassettes, assets, jobs |
| `ContentPilot.ArchitectureTests` | nothing | layering and tenant-isolation rules |
| `ContentPilot.RendererTests` | Chromium in `.playwright/` | 20 render and fidelity tests |
| `ContentPilot.IntegrationTests` | Docker (Testcontainers) | EF, storage, queue, tenancy, agent executor |
| `ContentPilot.WorkflowTests` | Docker | end-to-end ping skeleton |

`tests/Shared/DockerAvailability.cs` skips the Docker suites when it is absent. Run the
narrow suite while iterating; run all of them before merging (rule 5).

## Commands you do not need to rediscover

```bash
dotnet build                                  # warnings are errors
dotnet test tests/ContentPilot.UnitTests      # fast loop, no Docker
docker compose up --build                     # api :8080, renderer :8081, aspire dashboard
docker compose run --rm api --seed            # golden tenant
./scripts/lanes.sh                            # who owns what right now
export COMPOSE_PROJECT_NAME=contentpilot-<you>
```

Ports live in a git-ignored `.env` (`POSTGRES_PORT` defaults to 5433 — the host already has
5432). Never run `docker compose down -v`.

## Token discipline

1. Start here, not with `grep -r` over `src/`. This file names the file; read that file.
2. Read the plan by line range from the tables above. Never `cat IMPLEMENTATION-PLAN.md`.
3. `README.md` (313 lines) is for running things — check it before inventing a command.
4. For current ownership read `.claims/*.md` or run `./scripts/lanes.sh`. The handover notes
   at the end of `PARALLEL-WORK.md` are the "what just changed" log — read the tail, not the
   whole file.
5. Do not re-read a file you just wrote, and do not re-derive what the handover already says.
6. When you finish a phase, update the status column and the type index above. A stale map
   costs more than no map.
