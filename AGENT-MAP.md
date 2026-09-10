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
| 5 — Orchestrator, retries, self-correction | 1266 | partial (`claude`) — policy landed, worker loop not wired |
| 6 — Visual QA and Marketing QA | 1285 | unclaimed |
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
`TemplateSelector` (template eligibility computed from manifests). Execution and run
persistence: `Infrastructure/Ai/AgentExecutor.cs` with `AgentContext`. Prompts:
`Application/Prompts/` (`PromptLibrary`, `PromptTemplate`), registry in
`Infrastructure/Ai/PromptRegistry.cs`.

**Content memory** — `Application/ContentMemory/SimHash.cs`;
`Infrastructure/Branding/ContentMemoryReader.cs`.

**Quality (gate 1)** — `Domain/Quality/` (`QaFinding`, `QaFindingCode` — append-only and
grouped by hundreds; `QaFindingCodes.GateFor` says which gate owns which code —,
`QaSeverity`, `QaGate`, `QaOutcome`, `QualityReview`). `Application/Quality/`
(`DeterministicQaSuite`, `DeterministicQaInput`/`DeterministicQaOptions`, `QaReport`,
`Checks/` — `LayoutChecks`, `ContrastCheck`, `LogoChecks`, `FidelityChecks`,
`FileSanityChecks`). EF configuration in `Infrastructure/Persistence/Configurations/
QualityConfigurations.cs`. Calibration harness and threshold rationale:
`tests/ContentPilot.RendererTests/FidelityCalibrationTests.cs`, regenerating
`artifacts/fidelity-calibration.md` (git-ignored) on every run.

**Orchestration (policy only — no worker loop yet)** — `Application/Orchestration/`
(`ItemStateMachine` — legal §7 item transitions and loop-safety; `RemediationRouter` —
§8 finding-code-to-restart-step table plus the escalation ladder; `BudgetGuard` —
§24 reserve-then-commit). `Domain/Workflow/` (`WorkflowRun` — attempt counters and
deadline read from `Domain.Tenancy.TenantLimits`, `WorkflowStep` — append-only,
idempotency key `(RunId, StepName, Attempt)`, `BudgetReservation`); `ContentRevision`
lives in `Domain/Content/` next to `ContentItem`. EF configuration in
`Infrastructure/Persistence/Configurations/WorkflowConfigurations.cs`. Nothing yet calls
any of this from a job — see PARALLEL-WORK.md's phase 5 section for what is still missing.

**Brand Brain** — `Application/Brand/` (`BrandBrainAssembler`, `BrandSnapshot` and its views,
`BrandBlockRenderer`); port `Application/Capabilities/IBrandBrainReader.cs`; implementation
`Infrastructure/Branding/BrandBrainReader.cs`; seed `GoldenTenantSeeder.cs`; assets
`AssetLibrary.cs` and `Infrastructure/Assets/` (`ImageIngestor`, `SvgSanitizer`).

**Entities** — `Domain/Tenancy/Tenant`; `Domain/Branding/` (`Brand`, `BrandProfile` with
`ToneOfVoice` / `VisualIdentity` / `Messaging`, `BrandProfileVersion`, `BrandAsset`,
`ProductFact`, `AudiencePersona`, `ContentPreferences`, `IndustryProfile`);
`Domain/Content/` (`ContentCampaign`, `ContentItem`, `ContentHistoryEntry`);
`Domain/Observability/` (`AgentRun`, `CostEntry`, `PromptVersion`); `Domain/Quality/`
(`QualityReview`, see above). EF configurations mirror those under
`Infrastructure/Persistence/Configurations/`.

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
`BrandBrainEndpoints`, `AssetEndpoints`, `DiagnosticsEndpoints`.

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
