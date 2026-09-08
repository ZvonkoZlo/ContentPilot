# ContentPilot — Implementation Plan

**An autonomous content team, built as a boring pipeline.**

A phased implementation plan for ContentPilot: a .NET platform that turns a business's Brand Brain into a week of publishable social content, using a deterministic orchestrator, narrow LLM agents, and a template renderer that never lets a model redraw your product.

| | |
|---|---|
| **Stack** | .NET 9 · PostgreSQL · EF Core · Playwright · FFmpeg · S3 |
| **Shape** | Modular monolith, 3 deployables |
| **Pilot tenant** | Appointso |
| **Status** | Review draft v0.1 — no code written |

---

## 1. Executive architecture summary

The product idea is sound and the architectural instincts in your brief are mostly right. The single most important thing to internalise before writing code is this: **this is not an agent system with a rendering step. It is a rendering pipeline with a few LLM calls in it.**

Roughly 80% of the code, and close to 100% of the perceived output quality, lives in deterministic territory: template manifests, layout constraint enforcement, Chromium screenshotting, image compositing, FFmpeg filtergraphs, novelty checks, budget guards. The LLM does four genuinely useful things — pick topics, write copy, choose a creative direction, and look at a picture and say what is wrong with it. Everything else is software you control.

The architecture is organised around that split:

- **Agents** are stateless, single-purpose functions: typed input → versioned prompt → JSON-schema-constrained output → typed DTO → deterministic validator. They never call each other, never write state, and never decide what happens next.
- **Pipelines** (image, reel) are pure .NET + Chromium + FFmpeg. No model touches product pixels. Screenshot fidelity is guaranteed *by construction*, then verified as a regression check.
- **The orchestrator** is a persisted, resumable state machine in C#. It owns ordering, attempts, budgets, and terminal states. It is the only component that knows the shape of the workflow.
- **QA is a ladder**, not a model call: cheap deterministic gates first, a vision model only for what a computer genuinely cannot judge, and a structured critique whose failure code maps to a *specific* remediation — not a blind full-pipeline retry.

Deployment is three containers plus infrastructure: `Api`, `Worker`, `Renderer`. The Renderer is split out for one honest reason — Chromium and FFmpeg turn a 110 MB image into a 1.8 GB one, and render work is CPU/RAM-bound in a way request handling is not. That is not a microservice; it is a sidecar with an HTTP door.

> **The bet this plan makes.** Quality comes from a small library of genuinely well-designed templates plus disciplined constraint enforcement — not from a smarter agent graph. Budget your effort accordingly: four templates that look like a real designer made them will carry this product further than any orchestration cleverness.

---

## 2. Recommended architecture, in text

```
                         +--------------------------------------+
   Angular UI  ---------> |  ContentPlatform.Api  (ASP.NET Core) |
   (later)                |  brand CRUD - asset upload - campaign|
                          |  browse - download - ZIP - triggers  |
                          +-------------+------------------------+
                                        | enqueue (Postgres job table)
                                        v
   Cron (Hangfire) --------> +--------------------------------------+
   Manual "Generate Week" -> |  ContentPlatform.Worker              |
   Event (later) ----------> |  +--------------------------------+  |
                             |  | CampaignWorkflowOrchestrator   |  |
                             |  |  deterministic state machine   |  |
                             |  +--+--------------+--------------+  |
                             |     | campaign     | per-item        |
                             |     v              v                 |
                             |  Strategist   ItemWorkflow           |
                             |  (LLM)         |- CreativeDirector   |
                             |                |- Copywriter (LLM)   |
                             |                |- SpecValidator (det)|
                             |                |- ImagePipeline      |
                             |                |- ReelPipeline       |
                             |                |- DeterministicQA    |
                             |                |- VisualQA (vision)  |
                             |                |- MarketingQA (LLM)  |
                             |                +- RemediationRouter  |
                             +---+--------------+--------------+----+
                                 | HTTP         | SDK          | EF
                                 v              v              v
                     +--------------------+ +---------+ +------------+
                     | ContentPlatform.   | | LLM /   | | PostgreSQL |
                     | Renderer           | | image   | |  state     |
                     | Playwright/Chromium| | APIs    | |  runs      |
                     | + FFmpeg + Magick  | +---------+ |  costs     |
                     | POST /render/image |             |  history   |
                     | POST /render/reel  |             +------------+
                     | POST /compare      |
                     +---------+----------+
                               |
                               v
                     +----------------------+     +---------------+
                     | S3 / R2 / MinIO      |     | SMTP / Resend |
                     | assets - renders -   |     | weekly email  |
                     | campaign packages -  |     +---------------+
                     | LLM payload archive  |
                     +----------------------+

   Cross-cutting: OpenTelemetry --> OTLP collector --> Aspire / Seq / Tempo
```

### Per-item workflow — the hot path

```
 ContentItem (from strategy)
      |
      v
 +- CreativeDirector -------------------------------------+
 |  1. TemplateSelector (deterministic candidate set from  |
 |     template manifests x item constraints)              |
 |  2. LLM ranks candidates + fills creative direction     |
 |  3. emits CopyBrief with per-slot character budgets     |
 +------------------------+--------------------------------+
                          v
                     Copywriter (LLM, writes INTO slots)
                          v
                  SpecValidator (deterministic)
                  - slot budgets   - asset IDs exist
                  - claims subset of ProductFacts (lexical)
                  - aspect ratio supported by template
                          v
               BackgroundGenerator (image model, optional)
                          v
                  Renderer  ->  PNG / MP4 + mask render
                          v
   +--- Gate 1: DeterministicQA -------------------+
   | overflow - occlusion - contrast - logo bbox   |
   | screenshot SSIM/dE - safe area - file sanity  |
   +-----------+------------------+----------------+
          hard fail          pass / grey zone
               |                  v
               |        +--- Gate 2: VisualQA (vision LLM) ---+
               |        +-----------+-------------------------+
               |                    v
               |        +--- Gate 3: MarketingQA (LLM) -------+
               |        +-----------+-------------------------+
               v                    v
       QaFinding[] --------> RemediationRouter (deterministic table)
                                |
        +---------------+-------+--------+--------------+
        v               v                v              v
   re-render only  re-write slot   new background   fallback template
        +---------------+----------------+--------------+
                                |
                     attempts < Max AND cost < Budget ?
                          yes --> loop      no --> NeedsHumanReview
```

---

## 3. Major architecture decisions and why

| Decision | Choice | Why / what it rules out |
|---|---|---|
| **Workflow control** | Hand-written persisted state machine in C#; one row per run, one per step | Temporal, Dapr Workflows and Elsa all solve this well but each adds a server, a programming model, and a debugging surface. Your workflow is about ten states with one fan-out. Own it. Revisit Temporal only if you add human pauses lasting days. |
| **Job queue** | Postgres table + `FOR UPDATE SKIP LOCKED` poller; Hangfire for cron only | Keeps job state in the same transaction as domain state — no dual write, no "job ran but the campaign row never committed". Hangfire's value here is its scheduler and dashboard, not its queue. |
| **Agent output** | Provider-native structured output (JSON Schema) → typed DTO → deterministic validator | Never parse prose. The schema gets you shape; the validator gets you *truth* (asset IDs exist, budgets respected, claims supported). |
| **Product pixels** | Screenshots are composited, never generated or edited by a model | Removes the entire class of "AI redrew my UI" failures. Fidelity becomes a renderer regression test rather than an open-ended AI problem. |
| **Static rendering** | Razor → HTML/CSS → Playwright Chromium element screenshot at 2x | Endorsed. Drawing layouts in SkiaSharp or hand-built SVG costs an order of magnitude more effort for text wrapping, web fonts, gradients and shadows. |
| **Reel rendering** | MVP: Playwright-rendered PNG layers + FFmpeg filtergraph. Not Remotion. | Reuses the whole image pipeline, needs no Node runtime, has no licence question, is deterministic, renders in seconds. See §17. |
| **Image comparison lib** | Magick.NET — `Compare` ships SSIM / DSSIM / PSNR / RMSE | ImageSharp is a nicer API but its Six Labors Split Licence turns into a paid commercial licence once this is revenue-generating SaaS. Decide that now, not at launch. |
| **MCP** | Not in the MVP. Shape the capability interfaces so a façade is a day's work later. | See §4. |
| **Vector DB** | None. Deterministic SQL + SimHash for novelty. `pgvector` later, if ever. | "Do not repeat the last four weeks" is a `WHERE` clause, not a semantic search problem. |
| **Multi-tenancy** | `TenantId` on every row, EF global query filters, storage key prefixes — from day one | Costs about two days now; retrofitting costs a rewrite. But run one tenant: skip signup, billing, and role management entirely. |
| **Deployables** | Api, Worker, Renderer + Postgres + object storage | The Renderer split is justified by image size and resource profile. Nothing else in this system is. |

---

## 4. Should you use MCP? Mostly no — and here is exactly where yes

MCP solves one specific problem: letting an agent process *you do not control* discover and call tools across a process boundary. Its costs are a transport, a second process to supervise, JSON serialisation of things that were already objects, and a failure domain unrelated to your domain.

In your MVP the agent process *is* your process. The orchestrator is deterministic and knows exactly which data each agent needs before the call is made. Every tool you listed is a method you would otherwise write as `IBrandBrainReader.GetSnapshotAsync(brandId, ct)`. Introducing MCP there buys nothing and costs latency, debuggability, and a distributed failure you did not need.

> **Sharper point.** Several of your proposed MCP tools should not be tools at all. `save_campaign()`, `save_revision()` and `approve_asset()` are **orchestrator responsibilities**. If an agent can call them, you have handed workflow control to the LLM — the exact thing you said you did not want. Agents get read-only capabilities and pure functions. Every write goes through the orchestrator.

### Where MCP genuinely earns its place, later

- **An external, read-only Brand + Content MCP server.** So you, or a customer, can point Claude Desktop or an IDE at a brand and ask questions, draft a one-off post, or inspect content history. That is a real product feature, not plumbing.
- **Third-party template plugins**, if you ever let outside designers ship templates.
- **Customer-side data connectors** — pulling product facts from a customer's Notion or CRM through *their* MCP servers. Here you are the client, not the server, and MCP is unambiguously the right answer.

### What to do now so the option stays cheap

Put the read capabilities in `Application/Capabilities/` as interfaces with POCO inputs and outputs, a JSON Schema descriptor per method, and no `DbContext` or `HttpContext` in any signature. An MCP server then becomes a thin project that reflects over that registry.

**Verdict.** MVP: plain .NET interfaces, in-process, no MCP. Phase 10+: an internal-network-only, read-only MCP façade over Brand, Content and Assets, authenticated per tenant. Never expose image, video or content-write tools over MCP — those are orchestrator-owned.

---

## 5. Agent architecture — and three corrections to yours

Your seven-agent split is close to right, but it contains one sequencing bug and one category error that will cost you weeks if they reach the code.

### Correction 1 — the sequencing bug

You have Copywriter running *before* CreativeDirector. That guarantees text overflow. Templates have hard geometric limits: a headline slot is 2 lines at 64px in a 1080×1350 frame, which is roughly 42 characters. If the copywriter writes freely and the director picks a template afterwards, either the text is clipped or the director is forced into whichever template happens to fit — and template choice is a design decision, not a text-length accident.

**Fix:** split CreativeDirector into two passes. Pass A picks the template and emits a `CopyBrief` containing per-slot character budgets and tone notes. The Copywriter then writes *into named slots against those budgets*. Pass B assembles the final CreativeSpec. In practice pass B is mostly deterministic assembly with one small LLM call for background prompt wording.

### Correction 2 — the category error

ImageAgent and ReelAgent are not agents. They contain at most one LLM call (writing a background image prompt) and are otherwise deterministic pipelines. Calling them agents invites you to give them autonomy, retries of their own, and eventually the ability to "decide" things. Name them `ImageProductionPipeline` and `ReelProductionPipeline` and keep them in `Rendering`, not `Agents`. The rule: **if it does not call a language model, it is not an agent.**

### Correction 3 — template selection should not start with the LLM

Every template declares a manifest: supported aspect ratios, required asset kinds, slot list with budgets, content types it suits, and pillar affinities. Given a ContentItem, a deterministic `TemplateSelector` filters to the templates that are actually *legal* for it — usually two to five. Only then does the LLM rank those candidates and explain the direction. This makes an invalid template choice structurally impossible and makes the LLM call cheap and easy to evaluate.

### Final agent roster

| Component | Kind | In | Out | Scope |
|---|---|---|---|---|
| **ContentStrategistAgent** | LLM | BrandSnapshot, goals, ContentHistoryDigest (8 weeks), quota preferences | `ContentStrategy`: theme + N ContentItemPlans (type, topic, pillar, objective, day, priority) | Once per campaign |
| **CreativeDirectorAgent** (pass A) | LLM over deterministic candidates | ItemPlan, BrandVisualIdentity, legal template candidates, available assets | `CreativeDirection`: templateId, chosen assets, background intent, `CopyBrief` with slot budgets | Per item |
| **CopywriterAgent** | LLM | CopyBrief, tone, ProductFacts, audience, recent hooks to avoid | `CopySet`: hook, headline, slot texts, caption, CTA, hashtags, (reel) per-scene copy | Per item |
| **CreativeSpecAssembler** (pass B) | Deterministic + tiny LLM call | CreativeDirection + CopySet | Validated `CreativeSpec` + background prompt | Per item |
| **ImageProductionPipeline** | Deterministic | CreativeSpec | PNG/JPEG + mask render + render report | Per item / attempt |
| **ReelProductionPipeline** | Deterministic | CreativeSpec (scenes) | MP4 + cover PNG + render report | Per item / attempt |
| **DeterministicQaSuite** | Code | Render output + spec + source assets | `QaFinding[]` | Every attempt, first |
| **VisualQaAgent** | Vision LLM | Rendered image (and reel keyframes), spec summary, checklist | `QaFinding[]` with severity + slot targets | Only if deterministic gate passes or is ambiguous |
| **MarketingQaAgent** | LLM | CopySet, audience, ProductFacts, recent content digest | `QaFinding[]` | Once per item, in parallel with VisualQA |

### The agent contract

Every agent implements one interface and nothing else. No agent has access to `DbContext`, the storage client, or another agent.

```csharp
public interface IAgent<TInput, TOutput>
{
    string Name { get; }            // "content-strategist"
    string AgentVersion { get; }    // "2" - bump when I/O contract changes
    Task<AgentResult<TOutput>> ExecuteAsync(
        AgentContext ctx,           // tenant, workflowRunId, attempt, budget, trace
        TInput input,
        CancellationToken ct);
}

// AgentResult carries: TOutput, PromptVersionId, ModelId, TokenUsage,
// Cost, Duration, RawResponseRef (S3 key), ValidationOutcome.
```

The base class `LlmAgentBase` owns the shared machinery: render the versioned prompt template, attach the JSON schema, call the provider through the middleware pipeline, deserialise, run the agent's declared validators, record the AgentRun row, emit the trace span. An individual agent file ends up being a prompt reference, an output DTO, and a list of validators — typically under 100 lines.

### Validators are first-class

Every agent declares deterministic post-conditions that run before the orchestrator accepts the output. A validation failure is *not* an exception; it becomes a critique fed back into a retry of that same agent (max two schema-repair attempts, separate from the item-level attempt budget).

- **Strategist:** item count matches quota; no topic SimHash within the cooldown window; pillar distribution within bounds; every content type is one this tenant has enabled.
- **Copywriter:** every slot within budget; hashtag count in range; no forbidden claim phrases; every factual assertion maps to a ProductFact ID (agent must cite them).
- **CreativeDirector:** templateId is in the candidate set it was given; every asset ID exists and belongs to the tenant; aspect ratio supported.
- **QA agents:** every finding has a known `code` from the closed enum, a severity, and a target that resolves to a real slot or asset.

> **Why the closed finding enum matters.** If VisualQA can return free-text problems, remediation must be decided by another LLM call, and you are back to an uncontrolled loop. A closed enum of roughly 20 finding codes (`TextClipped`, `LowContrast`, `LogoDistorted`, `ScreenshotOccluded`, `ArtefactInBackground`, `ClaimUnsupported`, `HookGeneric`, `TopicRepetition`…) lets a static table decide what to do. Free text goes in an `observation` field used only as critique context for the retry.

---

## 6. Orchestrator design

Two orchestrators, one mechanism. `CampaignWorkflow` handles planning and packaging; `ContentItemWorkflow` handles one deliverable. Items are independent, so one bad reel cannot block a good carousel.

```
CampaignWorkflow
  Created -> Planning -> ItemsRunning -> Packaging -> Ready
                 |            |              |
                 +--> Failed  +--> PartiallyReady (some items need review)

ContentItemWorkflow  (one row per item, resumable, attempt-bounded)
  Pending -> Directing -> Writing -> SpecAssembly -> AssetGeneration
          -> Rendering -> Validating -> Approved
                              |
                              +-> Remediating -> (back to a specific earlier step)
                              +-> NeedsHumanReview | Failed
```

### Core loop

The orchestrator is a pure function over persisted state: `(WorkflowRun, WorkflowStep[]) => NextAction`. A worker leases a run, computes the next action, executes exactly one step, persists the result and the new state in one transaction, and re-enqueues. This gives you resumability after a crash, a full audit trail, and the ability to inspect or manually advance a stuck run from SQL.

```
1. Lease a workflow run           (SELECT ... FOR UPDATE SKIP LOCKED)
2. Load run + steps + budget ledger
3. NextAction = StateMachine.Decide(run, steps)
4. Guard:  attempts < max?  cost + estimate <= budget?  not cancelled?
5. Execute the single step (agent call, render call, or pure code)
6. In ONE transaction:
     - persist step result + AgentRun + CostEntry
     - transition run state
     - enqueue next job (or terminal state + campaign notification)
7. Release lease
```

### What the orchestrator owns exclusively

- Step ordering and the legality of every transition (illegal transition throws — it is a bug, not a runtime condition).
- Attempt counters, at three levels: per-step schema repair, per-item remediation, per-campaign.
- Budget reservation and commitment (§24).
- All persistence — agents return values, the orchestrator saves them.
- Trace/span creation and correlation IDs.
- Idempotency: every step writes an `IdempotencyKey` of `(itemId, step, attempt)`; a replayed job is a no-op.

> **Non-negotiable invariant.** No agent, no pipeline, and no QA component may enqueue a job, mutate a workflow row, or call another agent. Enforce it with an architecture test (NetArchTest) that fails the build if `ContentPlatform.Agents` references `IJobQueue`, `DbContext`, or any repository. This one test is what keeps the "no uncontrolled agent loops" promise real over the next twelve months.

---

## 7. Workflow state machine

| State | Level | Entered when | Exits to |
|---|---|---|---|
| `Draft` | Campaign | Created by user or scheduler, not yet queued | Planning, Cancelled |
| `Planning` | Campaign | Job leased; strategist running | ItemsRunning, Failed |
| `ItemsRunning` | Campaign | Strategy validated; N item workflows created | Packaging (all items terminal) |
| `Packaging` | Campaign | All items terminal; building plan.json, folders, ZIP | Ready, PartiallyReady, Failed |
| `Ready` | Campaign | All items Approved, package built, email sent | terminal |
| `PartiallyReady` | Campaign | At least one item Approved, at least one needs review | Ready (after human action), terminal |
| `Pending` | Item | Created by strategy fan-out | Directing |
| `Directing` / `Writing` / `SpecAssembly` | Item | Sequential agent steps | next step, Remediating, Failed |
| `AssetGeneration` | Item | Spec valid; background image generation if required | Rendering |
| `Rendering` | Item | Renderer call in flight | Validating, Remediating |
| `Validating` | Item | QA ladder running | Approved, Remediating |
| `Remediating` | Item | Findings mapped to a remediation action | the targeted earlier step, NeedsHumanReview |
| `Approved` | Item | QA ladder clean, or human approved | terminal |
| `NeedsHumanReview` | Item | Attempts or budget exhausted; last render retained | Approved / Rejected by human |
| `Failed` | Both | Non-recoverable: bad config, missing required asset, provider outage past retry | terminal |

> **Design detail worth keeping.** `NeedsHumanReview` is not a failure and must never discard work. The best attempt so far — scored by finding severity — is promoted to the item's current asset and appears in the UI with its findings listed. A week where three of five posts are perfect and two need a tweak is a good week; a week where two posts vanished is a broken product.

---

## 8. Retry and self-correction

The generic mechanism is a `RemediationRouter`: a static, testable mapping from finding code to the earliest step that could plausibly fix it. Blind full-pipeline retries are banned — they are slow, expensive, and non-convergent.

| Finding code | Source | Remediation | Restart at |
|---|---|---|---|
| `TextClipped`, `TextOverflow` | Deterministic | Rewrite the offending slot with a budget reduced by 15% | Writing (slot-scoped) |
| `LowContrast` | Deterministic | Switch slot to the template's alternate colour scheme; if already alternate, regenerate background darker/lighter | SpecAssembly |
| `ScreenshotOccluded`, `ScreenshotDistorted` | Deterministic | Renderer bug or bad spec — drop to fallback template | Directing (template excluded) |
| `LogoDistorted`, `LogoTooSmall` | Both | Force logo lockup preset; re-render only | Rendering |
| `BackgroundArtefact`, `BackgroundOffBrand` | Visual QA | Regenerate background with critique appended and a new seed | AssetGeneration |
| `ClaimUnsupported` | Marketing QA | Rewrite copy with the offending claim explicitly forbidden | Writing |
| `HookGeneric`, `CtaWeak` | Marketing QA | Rewrite hook/CTA only, with the critique and three recent good examples | Writing (slot-scoped) |
| `TopicRepetition` | Deterministic or MQA | Escalate to campaign level: strategist re-plans this one item with the topic banned | Planning (single item) |
| `CompositionWeak` | Visual QA | Try the next-ranked template candidate | Directing |

### The escalation ladder

Attempts are not identical retries. Each attempt must change something, or the loop cannot converge.

```
Attempt 1  same inputs + structured critique appended
Attempt 2  change exactly ONE variable
           (tighter copy budget | new background seed | next template)
Attempt 3  fallback: the template flagged SafeMode in its manifest -
           generous slot budgets, solid brand-colour background,
           no generated imagery. Almost always renders cleanly.
Attempt 4+ does not exist. -> NeedsHumanReview
```

### Three independent counters

- **Schema repair** — max 2. The model returned invalid JSON or failed a validator. Cheap; does not consume item attempts.
- **Transient** — max 3 with exponential backoff and jitter. Provider 429/5xx, renderer timeout. Does not consume item attempts and does not count as a quality failure.
- **Quality** — max 3 per item, configurable per tenant. This is the ladder above.

> **Loop safety, belt and braces.**
> - Every workflow run has a hard wall-clock deadline (default 45 minutes). Past it, the run is force-terminated to `NeedsHumanReview`.
> - Total steps executed per item is capped (default 40) regardless of which counters say what.
> - A remediation may never target a step *later* than the one that produced the finding — that would be a cycle with no state change.
> - The same (finding code, target) pair appearing twice in a row escalates one rung immediately instead of retrying the same fix.

---

## 9. QA architecture

Three gates, cheapest first. Roughly 70% of real defects are caught by gate 1 at essentially zero cost, and running gate 1 first also stops you paying a vision model to notice something a bounding box already proved.

### Gate 1 — deterministic, always runs

The renderer returns a **render report** alongside the image: for every slot, the DOM-measured bounding box, whether `scrollHeight > clientHeight` (overflow), computed colours, and the z-order occlusion map. This is enormously more reliable than looking at pixels, because the browser already knows the answer.

- **Text overflow / clipping:** read directly from the render report. Zero false positives.
- **Safe areas:** no content inside the platform-specific margins (Instagram UI overlay zones, TikTok caption band).
- **Contrast:** WCAG ratio computed between each text slot and the sampled median luminance of the pixels actually behind it.
- **Logo integrity:** rendered logo bounding box aspect ratio vs source within 0.5%; minimum size; clear-space rule from the brand profile.
- **Screenshot fidelity:** §10.
- **File sanity:** dimensions, colour profile, byte size, MP4 duration/fps/audio track presence, first frame is not black.

### Gate 2 — VisualQA, vision model

Only asked to judge what code cannot: does this look like a competent designer made it, does the background contain artefacts (extra fingers, garbled pseudo-text, warped geometry), is it on-brand, does the composition read at thumbnail size. Give it the image at full size *and* a 150 px thumbnail — thumbnail legibility is how social content is actually consumed and models judge it well when asked explicitly. It returns findings from the closed enum, with a confidence value; low-confidence findings below a threshold are recorded but do not trigger remediation.

### Gate 3 — MarketingQA, text model

Runs in parallel with gate 2, on text only, so it is cheap. Its most important job is **claim grounding**: every factual assertion in the copy must cite a ProductFact ID, and this agent verifies the citation actually supports the claim. Also hook strength, audience fit, CTA presence, and repetition against the recent-content digest.

> **A warning about LLM-judge drift.** Vision QA agents that are asked "is anything wrong with this image?" will always find something — which turns into infinite remediation and burned budget. Two counters: (1) ask for findings only at or above a defined severity, with an explicit instruction that a merely-fine image should return an empty array; (2) track the *QA pass rate* as an operational metric. If fewer than 60% of first attempts pass, your QA prompt is miscalibrated, not your renderer. Include known-good goldens in the eval suite specifically to detect this.

---

## 10. Deterministic screenshot validation

Start from the strongest available position: **make the failure impossible, then verify you did.**

1. The source screenshot is stored immutably with a SHA-256 content hash. It is never sent to an image model, never inpainted, never "enhanced".
2. The spec validator asserts that any asset placed in a slot marked `immutable: true` in the template manifest has `origin = UserUpload`. A generated asset in an immutable slot is a hard error, not a QA finding.
3. The renderer places it with `image-rendering` defaults, no CSS filters, no blend modes, uniform scale only — the manifest forbids non-uniform transforms on immutable slots, and this is checked in the render report.

What remains is a *regression check against renderer bugs*: a stray overlay, a wrong crop, an unintended opacity, a gradient scrim that swallows the UI. Here is the method.

### Mask-render extraction

Render the template twice in the same browser context:

```
Pass A  normal render                    -> final.png
Pass B  ?mask=slot-screenshot            -> mask.png
        every element except the target slot painted
        solid magenta; the slot painted solid white
```

Pass B gives you, exactly and without any image analysis: the slot's device-pixel bounding box, and the **occlusion percentage** (how much of the slot is covered by anything else). Occlusion above the manifest's allowance is an immediate deterministic fail. Pass B costs ~200 ms because the page is already loaded.

### Scale-normalised comparison

The critical subtlety you flagged: the screenshot is legitimately scaled. Comparing a 2400×1600 original against a 700×467 rendered region will fail on any pixel metric, because downscaling genuinely destroys high-frequency detail. So normalise *both* sides through the same path:

```
1. Crop final.png to the slot bbox from mask.png      -> rendered
2. Resize the ORIGINAL asset to exactly the rendered
   size using the same filter Chromium used (Lanczos,
   Magick.NET FilterType.Lanczos)                     -> reference
3. Both to linear RGB, strip alpha over a known matte
4. Compute the metric set below
```

| Metric | Catches | Notes |
|---|---|---|
| **Occlusion %** (from mask) | Overlays, scrims, elements on top | Exact. Not a heuristic. Check this first. |
| **Aspect-ratio delta** | Squashing, stretching | From the render report; tolerance 0.5%. |
| **MS-SSIM** on luminance | Structural change, blur, warping, wrong crop | Primary metric. Magick.NET `Compare(ErrorMetric.StructuralSimilarity)`. |
| **Mean ΔE2000** on a 16×16 grid | Tints, opacity changes, blend modes, wrong colour profile | SSIM on luminance is nearly blind to a uniform colour cast — this covers the gap. |
| **Sobel edge-map IoU** | Geometric warping that SSIM tolerates | Optional; add only if the golden set shows SSIM missing warps. |
| **pHash distance** | Completely wrong image in the slot | Fast pre-filter; Hamming > 12 means it is not even the same picture — fail immediately, skip the rest. |

### The policy

```
occlusion > manifest.maxOcclusion            -> FAIL (deterministic)
aspectDelta > 0.005                          -> FAIL
pHashDistance > 12                           -> FAIL
msSsim >= 0.97  AND  meanDeltaE <= 2.0       -> PASS
msSsim in [0.90, 0.97)  OR  dE in (2.0, 5.0] -> ESCALATE to VisualQA
                                                (send crop + reference
                                                 side by side, ask only
                                                 "is the UI altered?")
msSsim < 0.90   OR  meanDeltaE > 5.0         -> FAIL
```

> **Calibrate, do not guess.** Those numbers are starting points, and the right values depend on your screenshots and your scale factors. Build a calibration harness in Phase 4: take 20 real Appointso screenshots, render each through every template at every supported scale, and record the metric distribution for known-good renders. Then inject known-bad mutations — 10% opacity overlay, 2 px blur, 5% horizontal squash, a 12 px gradient scrim, a JPEG quality-40 pass — and set thresholds where the two distributions separate. This half-day of work is what makes the whole QA story trustworthy, and it doubles as an eval fixture.

**On OCR:** you almost certainly do not need it, and it will cost you more in false positives than it earns. The one case where it is justified is verifying that a specific piece of UI text (a price, a business name) is legible at the final render size — and even there, "is this text legible?" is better answered by rendering at target size and asking the vision model once, than by running Tesseract and arguing about confidence scores.

---

## 11. Suggested .NET solution structure

Your proposal is close, but it has too many projects. Seven agent projects and five MCP projects is thirteen csproj files that will never be referenced independently — each one is build time, a dependency graph edge, and a place for a circular reference to hide. Folders inside one project give you the same modularity with none of the ceremony. Extract a project only when something needs a genuinely different dependency set or deployment target.

```
ContentPilot.sln
src/
  ContentPilot.Domain/                 no dependencies at all
    Tenancy/  Branding/  Content/  Workflow/  Observability/
    Common/ (ValueObjects, Result, Guard, Ids)

  ContentPilot.Application/            references Domain
    Abstractions/        IJobQueue, IObjectStore, IClock, IUnitOfWork,
                         ILanguageModelClient, IImageGenerationClient
    Capabilities/        read-only capability interfaces (future MCP surface)
    Agents/              Strategist/ Copywriter/ CreativeDirector/
                         VisualQa/ MarketingQa/ Shared/ (LlmAgentBase)
    Orchestration/       CampaignWorkflow, ContentItemWorkflow,
                         StateMachine, RemediationRouter, BudgetGuard
    Prompts/             .prompt.md files as embedded resources + loader
    Quality/             DeterministicQaSuite, checks/
    Campaigns/           use cases: GenerateWeek, PackageCampaign, Approve
    Brand/               BrandBrain assembly + snapshotting
    ContentMemory/       digest builder, SimHash, cooldown rules

  ContentPilot.Infrastructure/         references Application
    Persistence/         AppDbContext, configurations, migrations, repos
    Jobs/                Postgres queue, leasing, Hangfire cron host
    Storage/             S3/R2/MinIO object store, presigned URLs
    Ai/                  OpenAI / Anthropic / image-model adapters,
                         middleware (retry, cost, tracing, cassette)
    RendererClient/      typed HTTP client for the Renderer service
    Email/               SMTP / Resend sender + templates
    Telemetry/           OTel setup, exporters

  ContentPilot.Rendering.Contracts/    shared DTOs: RenderRequest,
                                       RenderReport, TemplateManifest
                                       (referenced by BOTH Application
                                        and Renderer - no other coupling)

  ContentPilot.Renderer/               its own ASP.NET minimal API service
    Templates/           Razor components + template.manifest.json
      static/  hook-overlay, phone-floating, feature-highlight,
               testimonial, stat-card, safe-mode
      carousel/ problem-solution-3, feature-tour-5
      reel/     problem-solution, feature-tour, before-after
    Fonts/               embedded woff2, no network fetch
    Engine/              RazorRenderer, PlaywrightPool, MaskRenderer
    Video/               SceneComposer, FFmpegRunner, filtergraph builder
    Imaging/             MagickComparer (SSIM, dE2000, pHash)
    Endpoints/           /render/image /render/carousel /render/reel /compare

  ContentPilot.Api/                    thin: endpoints, auth, validation
  ContentPilot.Worker/                 thin: hosted services, DI wiring

tests/
  ContentPilot.UnitTests/
  ContentPilot.ArchitectureTests/      NetArchTest rules (see 6)
  ContentPilot.IntegrationTests/       Testcontainers: pg + minio
  ContentPilot.WorkflowTests/          full workflow, faked providers
  ContentPilot.RendererTests/          golden-image, runs in prod image
  ContentPilot.Evals/                  agent evals, cassette + live modes
  ContentPilot.GoldenData/             fixture tenant, assets, baselines
```

> **Deliberate omissions.** No `ContentPilot.Mcp` (§4). No `ContentPilot.Orchestrator` project — orchestration is application logic and splitting it out creates a circular pull toward Infrastructure. No separate `Agents` project — it would need Application types anyway. `Rendering.Contracts` is the only new project, and it exists to keep the Renderer service from referencing your domain.

### Architecture tests to write on day one

- `Domain` references nothing but the BCL.
- `Application.Agents.*` may not reference `DbContext`, `IJobQueue`, `IObjectStore`, or any type under `Orchestration`.
- `Renderer` may not reference `Domain`, `Application`, or `Infrastructure`.
- Every `IAgent<,>` implementation has a matching prompt file and at least one validator.
- Every entity implementing `ITenantOwned` has a global query filter registered (reflection test over the model).

---

## 12. Domain entities and relationships

```
Tenant 1---* Brand 1---1 BrandProfile
                    |          |
                    |          *---> BrandProfileVersion  (immutable JSONB snapshot + hash)
                    |---* BrandAsset      (logo, screenshot, photo, prior creative)
                    |---* ProductFact     (atomic, citable claim)
                    |---* AudiencePersona
                    |---1 ContentPreferences

Brand 1---* ContentCampaign  (one per week, or ad hoc / event)
              |---1 ContentPlan             (strategist output, versioned)
              |---* ContentItem
              |        |---* CreativeSpec       (one per attempt, immutable)
              |        |---* ContentAsset       (rendered artefact)
              |        |---* ContentRevision    (attempt journal)
              |        |---* QualityReview      (one per QA gate per attempt)
              |---1 CampaignPackage         (manifest + ZIP key)
              |---* WorkflowRun
                       |---* WorkflowStep
                                |---0..1 AgentRun ---* CostEntry
Brand 1---* ContentHistoryEntry   (denormalised, written when item is Approved)
PromptVersion  (global, immutable)  <--- referenced by AgentRun
TemplateVersion (global, immutable) <--- referenced by CreativeSpec
```

| Entity | Key fields | Lifecycle notes |
|---|---|---|
| **Tenant** | Name, Plan, RetentionDays, DefaultBudgets | One row for now. Root of every query filter. |
| **Brand** | TenantId, Name, Website, Languages[], Timezone | A tenant may eventually have several brands; model it now, expose one. |
| **BrandProfile** | Tone JSONB, VisualIdentity JSONB, Messaging JSONB, Forbidden JSONB | Mutable working copy the user edits in the UI. |
| **BrandProfileVersion** | SnapshotJson, ContentHash, CreatedAt | **Immutable.** A snapshot is taken when a campaign starts; every AgentRun in it references that version. Without this you cannot reproduce or explain any past output. |
| **BrandAsset** | Kind, StorageKey, Sha256, Width/Height, Origin, Tags[], IsImmutableSource | Never mutated. Re-uploads create new rows. Derived variants (thumbnails, background-removed) are separate rows linked by ParentAssetId. |
| **ProductFact** | Key, Statement, Category, Evidence, IsPublic, ValidFrom/To | The claim-grounding source of truth. Copy must cite these by ID. |
| **AudiencePersona** | Name, Segment, Pains[], Goals[], Objections[], Vocabulary[] | Strategist and MarketingQA both read these. |
| **ContentPreferences** | PostsPerWeek, CarouselsPerWeek, ReelsPerWeek, ExcludedTopics[], PreferredTopics[], PublishDays[] | Drives quota validation on strategist output. |
| **ContentCampaign** | BrandId, WeekStart, TriggerType, Status, BrandProfileVersionId, BudgetCents | Unique on (BrandId, WeekStart, TriggerType) so a double-fired cron cannot duplicate a week. |
| **ContentPlan** | CampaignId, Theme, RawStrategyJson, PlanVersion | Versioned so a re-plan keeps the original. |
| **ContentItem** | ContentType, Topic, Pillar, Objective, PublishDay, Status, Attempts, BestAttemptId | The unit of work, retry, and budget. |
| **CreativeSpec** | ItemId, Attempt, TemplateVersionId, SpecJson, SpecHash | **Immutable per attempt.** The exact render input. Re-rendering a spec must reproduce the asset byte-for-byte (modulo generated backgrounds, which are referenced by asset ID, not regenerated). |
| **ContentAsset** | ItemId, Attempt, Kind (image/slide/video/cover), StorageKey, Sha256, Bytes, Meta JSONB | Every attempt's output is kept until retention; the UI shows the promoted one. |
| **ContentRevision** | ItemId, Attempt, Reason, FindingsJson, RemediationAction | The human-readable story of "why does attempt 3 exist". |
| **QualityReview** | ItemId, Attempt, Gate, Outcome, Score, FindingsJson, AgentRunId? | One row per gate per attempt, deterministic gates included. |
| **ContentHistoryEntry** | BrandId, PublishedOrApprovedAt, ContentType, Topic, TopicSimHash, Hook, HookSimHash, Pillar, TemplateId, AssetIds[], QualityScore | Written on approval. This is what the strategist reads, and it is deliberately denormalised so retrieval is one indexed query. |
| **WorkflowRun / WorkflowStep** | Scope, EntityId, State, Attempt, LeaseUntil, IdempotencyKey, Error | The orchestrator's state. Steps are append-only. |
| **AgentRun** | StepId, AgentName, AgentVersion, PromptVersionId, ModelId, InputRef, OutputRef, Tokens, Cost, Duration, Outcome, Attempt | Payloads live in object storage; only references and metrics in Postgres. |
| **PromptVersion** | PromptId, Version, ContentHash, Body, SchemaJson, ModelProfile, Params | Immutable, upserted from embedded resources at startup. |
| **TemplateVersion** | TemplateId, Version, ManifestJson, ContentHash | Same pattern. A CreativeSpec pins one, so old renders stay explainable after a template redesign. |
| **CostEntry** | Scope (item/campaign), AgentRunId?, Provider, Kind, Units, UnitPriceSnapshot, Cents | Append-only ledger. Budgets are sums over this table. |
| **CampaignPackage** | CampaignId, ManifestJson, ZipKey, BuiltAt, EmailSentAt | Rebuilt on demand if an item is approved after the fact. |

---

## 13. Database and storage design

### PostgreSQL conventions

- **Keys:** UUIDv7 primary keys (time-ordered, index-friendly, safe to expose in URLs, no cross-tenant enumeration). Not `int`.
- **Tenancy:** `TenantId` non-nullable on every tenant-owned table; EF global query filter from an `ITenantContext`; a composite index leading with `TenantId` on every hot query path. Row-level security in Postgres is worth adding at the point you take real customers, not before.
- **JSONB where the shape is open or versioned:** brand profile snapshots, creative specs, findings, strategy output, template manifests, asset metadata. Relational where you filter, join, or aggregate: items, runs, costs, history, facts.
- **Enums as smallint** with C# enum mapping, not Postgres enum types — adding a value to a Postgres enum inside a transaction is a known nuisance.
- **Money as integer cents** (`bigint`, micro-cents if you prefer — LLM calls can cost fractions of a cent). Never `double`.
- **Timestamps** `timestamptz`, UTC everywhere; the brand's timezone is used only for scheduling and publish-day labels.
- **Append-only tables** (`WorkflowStep`, `CostEntry`, `AgentRun`, `ContentRevision`) get no update path in code; enforce with a trigger if you like belt and braces.

#### Indexes that matter from the start

```
ContentHistoryEntry (TenantId, BrandId, ApprovedAt DESC)      -- digest query
ContentHistoryEntry (TenantId, BrandId, TopicSimHash)          -- novelty
ContentItem         (TenantId, CampaignId, Status)
WorkflowRun         (State, LeaseUntil) WHERE State = 'Runnable' -- partial
JobQueue            (RunAt, State) WHERE State = 'Pending'       -- partial
CostEntry           (TenantId, CampaignId) INCLUDE (Cents)     -- budget sums
AgentRun            (WorkflowRunId, StartedAt)
BrandAsset          (TenantId, BrandId, Kind)
```

### Object storage layout

```
s3://contentpilot/
  t/{tenantId}/
    brand/{brandId}/assets/{assetId}/{sha256}.{ext}     immutable, versionless
    brand/{brandId}/derived/{assetId}/{variant}.{ext}   thumbs, bg-removed
    campaigns/{campaignId}/
        plan.json
        post-01/ image.png  caption.txt  metadata.json
        post-02/ slide-01.png ... caption.txt
        reel-01/ reel.mp4  cover.png  caption.txt  script.txt  metadata.json
        package.zip
    campaigns/{campaignId}/_attempts/{itemId}/{attempt}/...   retained per policy
    runs/{workflowRunId}/agent/{agentRunId}/{input|output}.json
```

Rules: buckets are private, always; every read from the UI goes through a presigned URL with a 15-minute TTL minted by the API after an authorisation check; keys are opaque and always tenant-prefixed so a bucket policy can enforce isolation later; content-addressed asset paths mean uploading the same logo twice costs nothing and makes cache invalidation moot.

Use MinIO locally and in CI, Cloudflare R2 in production — R2's zero egress fee matters when the product is literally "download your images", and its S3 API is close enough that the same client works. Keep the abstraction to a five-method `IObjectStore` so the swap stays trivial.

---

## 14. Brand Brain design

The Brand Brain is not one blob. Split it by how each piece is *used*, and you avoid both the "everything is JSON so nothing is queryable" trap and the "40 tables for a form" trap.

| Content | Where | Why |
|---|---|---|
| Name, website, languages, timezone, industry | Relational columns | Queried, filtered, displayed everywhere. |
| Visual identity: colour roles, typography, logo variants, spacing, style keywords | JSONB on BrandProfile, shaped as design tokens | Consumed wholesale by the renderer as CSS custom properties. Never joined. Shape will evolve. |
| Tone, messaging principles, preferred CTA style, forbidden claims, banned words | JSONB | Goes into prompts as a block. Never queried field-by-field. |
| Product facts | **Relational rows** | Must be individually citable by ID for claim grounding, individually editable, and time-bounded. This is the one place people are tempted to use JSON and should not. |
| Audience personas | Relational row + JSONB detail | Selected per campaign; the detail is free-form. |
| Content preferences and quotas | Relational | Validated against numerically. |
| Assets | Metadata relational, bytes in object storage | Include perceptual hash and dominant colours in metadata for later dedup and background-matching. |
| Full profile snapshot per campaign | JSONB in BrandProfileVersion, content-hashed | Reproducibility. Non-negotiable. |
| Embeddings | Nowhere, yet | See below. |

### The snapshot and the prompt block

At campaign start, `BrandBrainAssembler` composes every source into one `BrandSnapshot` object, hashes it, and stores it as a `BrandProfileVersion`. A second function renders that snapshot into a compact Markdown *brand block* injected into every agent prompt. Two properties matter: it is deterministic (same snapshot, same bytes) and it is budgeted (target under 1,200 tokens; assert it in a test). Agents receive the snapshot, never the live database.

> **On the vector database question.** Do not add one. The Brand Brain for a single business is a few kilobytes of text — put all of it in the prompt and you get better results than any retrieval scheme. If a customer eventually has 500 product facts, add `pgvector` to the Postgres you already run and do similarity selection over *facts only*. That is a Phase-12 concern at the earliest, and Postgres will handle it without a new piece of infrastructure.

### Keeping Appointso out of the core

Nothing above is booking-specific, and that is the point. Industry-specific knowledge lives in exactly two places, both data:

1. A seedable `IndustryProfile` reference table — suggested pillars, typical pains, vocabulary, seasonality hints — that pre-fills a new brand's profile at onboarding and is then just editable brand data.
2. Prompt *examples* selected from the tenant's own content history, never hard-coded.

If you ever find yourself writing `if (industry == "salon")` in application code, the design has failed.

---

## 15. Content memory

Two mechanisms, and the second is the one that actually works.

### 1. The digest — give the strategist context

One indexed query returns the last eight weeks of `ContentHistoryEntry`, compressed into a compact block: date, type, pillar, topic, hook, template, quality score. Sixty rows fits in a few hundred tokens. Order it newest-first and mark the last four weeks explicitly as *hard-excluded*.

### 2. The novelty validator — do not trust the model to remember

After the strategist returns, before anything else happens, run a deterministic check on every proposed item:

```
topicSimHash   = SimHash(normalise(topic + pillar))       64-bit
hookSimHash    = SimHash(normalise(hook), 3-gram shingles)

REJECT item if any of:
  Hamming(topicSimHash, any topic in last 4 weeks) <= 6
  Jaccard(topic tokens, any topic in last 4 weeks) >= 0.6
  templateId used more than twice in the last 2 weeks
  pillar used for > 50% of items in the last 3 weeks
  topic matches an entry in ContentPreferences.ExcludedTopics

-> regenerate ONLY the rejected items, passing back the exact
   colliding entries as critique.  Max 2 novelty retries, then
   accept with a RepetitionRisk warning attached to the item.
```

SimHash over token shingles is a few dozen lines, needs no dependency, and is dramatically more reliable than asking a model to remember what it wrote three weeks ago. Normalisation should lowercase, strip stopwords, and lemmatise crudely (strip common suffixes) — do not over-engineer it.

A `ContentHistoryEntry` is written when an item reaches `Approved`, not when it is generated — content that never passed QA should not block future topics. Add an optional `PublishedAt` the user can set from the UI later; when direct publishing arrives, that field starts filling itself.

---

## 16. Rendering architecture: static images

Your proposed pipeline is right. Here is the version with the sharp edges filed off.

```
CreativeSpec
   |
   +-> TemplateVersion.ManifestJson   (slots, budgets, safe areas, aspect)
   |
   +-> Razor component render         Template.cshtml + BrandTokens
   |      brand tokens  -> CSS custom properties
   |      slot copy     -> escaped text nodes
   |      assets        -> data: URIs OR loopback URLs (never remote)
   |      fonts         -> @font-face with embedded base64 woff2
   |
   +-> HTML string
   |
   +-> Playwright page (pooled browser context)
   |      SetContentAsync(html)
   |      route interception: ABORT every external request
   |      await document.fonts.ready
   |      await Promise.all([...img].map(i => i.decode()))
   |      *,*::before,*::after { animation:none!important;
   |                             transition:none!important }
   |      viewport = frame size, deviceScaleFactor = 2
   |
   +-> ElementHandle("#frame").ScreenshotAsync(png)     -> final.png
   +-> evaluate(collectRenderReport)                    -> report.json
   +-> re-render with ?mask=slotId per immutable slot   -> mask.png
   |
   +-> Magick.NET: strip metadata, optional JPEG/WebP variant, sRGB
   |
   +-> upload + return {assetKey, report, masks}
```

### Determinism rules — every one of these has bitten someone

- **Pin the Chromium build.** Pin the Playwright package version and never let the base image float. A Chromium minor bump changes text rasterisation and every golden-image test fails at once.
- **Embed fonts, block the network.** A missing Google Fonts request produces a silent fallback and a subtly wrong render that QA may well pass. Abort all external requests at the route level so the failure is loud.
- **Kill animation and randomness.** No CSS animation, no `Math.random`, no `Date.now` in templates.
- **Fixed frame element.** Screenshot a sized element, not the viewport — scrollbars and viewport rounding are a classic source of one-pixel drift.
- **Pool browsers, isolate contexts.** One long-lived browser, a new `BrowserContext` per render, hard timeout, recycle the browser every N renders to cap memory. Concurrency limited by a semaphore sized to CPU count.

### The template manifest — the most important file in the system

```json
{
  "templateId": "phone-floating", "version": 3,
  "contentTypes": ["static"], "aspectRatios": ["4:5","1:1"],
  "pillarAffinity": ["problem-solution","feature-highlight"],
  "safeMode": false,
  "requiredAssets": [
    { "slot":"screenshot", "kind":"ProductScreenshot",
      "immutable": true, "maxOcclusion": 0.02,
      "transforms": ["uniformScale","cornerRadius","dropShadow"] }
  ],
  "slots": [
    { "id":"headline", "type":"text", "maxChars":42, "maxLines":2,
      "minFontPx":44, "shrinkToFit": true },
    { "id":"subhead",  "type":"text", "maxChars":90, "maxLines":3 },
    { "id":"cta",      "type":"text", "maxChars":24, "maxLines":1 },
    { "id":"logo",     "type":"asset", "kind":"Logo",
      "minWidthPx":110, "clearSpaceRatio":0.25 }
  ],
  "colorSchemes": ["light-on-dark","dark-on-light"],
  "safeAreas": { "instagram": {"top":0.08,"bottom":0.14} }
}
```

The manifest is consumed by three different components — TemplateSelector (legality), CopyBrief generation (budgets), and DeterministicQA (thresholds) — which is precisely why it must be data rather than knowledge spread across code. `shrinkToFit` deserves a note: allowing a small, bounded font-size reduction (say down to `minFontPx`) absorbs most near-miss overflows without a retry, and the render report records that it happened so QA can still flag abuse.

### Carousels

A carousel is not a special case. It is N specs sharing a `CarouselGroupId`, rendered with a shared style seed so the slides look like a set, plus one deterministic continuity check (same palette, same logo placement, slide numbering correct, first slide carries the hook). No new machinery.

---

## 17. Rendering architecture: Reels

> **Recommendation: skip Remotion for the MVP.** Remotion is genuinely excellent and I would reach for it if you needed spring-physics animation of live UI. But it means a Node runtime beside your .NET one, a React codebase your templates must be duplicated into, a headless-Chromium frame-by-frame render (450 frames for a 15-second reel, in the tens of seconds), and a company licence question you have to answer before launch. For template-driven reels you can get 90% of the visual result from layers you already know how to render.

### Recommended MVP approach: layered PNGs + FFmpeg filtergraph

```
ReelSpec { durationSec, aspect: "9:16", scenes: [...] }

for each scene (typically 3-5, 3-5 seconds each):
    render 2-3 PNG layers with the SAME Playwright pipeline as static:
      layer 0  background      (generated image or brand gradient)
      layer 1  device/product  (screenshot in frame, transparent PNG)
      layer 2  text + logo     (transparent PNG, exact same typography
                                rules and manifest budgets as static)

FFmpeg does the motion (one filtergraph, one pass):
    zoompan          slow push-in / pull-out on background   (Ken Burns)
    overlay + xy expr slide/rise for the product layer
    overlay + fade   text layer in/out with easing via sendcmd or
                     pre-baked alpha curves
    xfade            transitions between scenes (fade, wipeleft, slideup)
    afade + amix     licensed music bed, ducked
    format=yuv420p, +faststart, CRF 20, 30fps, 1080x1920, H.264/AAC
    -> reel.mp4
cover.png = the first scene composite, rendered at full quality
```

Properties: no Node, no new language, reuses the entire template and QA stack, renders in roughly two to five seconds, fully deterministic, and every text layer inherits the same overflow and contrast checks as static images. The limitation is real but acceptable: motion is per-layer transforms, not per-element choreography. For "hook → problem → product → CTA" reels, that is what template-based reels look like anyway.

#### Reel QA

Do not send video to a vision model. Extract keyframes — the midpoint of each scene plus each transition boundary — and run the static image QA on those, plus deterministic checks on the container: duration within tolerance, fps, resolution, no black frames, audio track present and peaking below −1 dBFS, and every text layer visible for at least 1.2 seconds (readability, computed from the scene plan, not from pixels).

#### The trigger to revisit

Move to Remotion when you want animated product UI (a cursor clicking through a booking flow), typographic choreography per word, or data-driven motion. That is a Phase-11+ decision and, done then, it slots in as an alternative `IReelRenderer` behind the same contract. Design that interface now; implement only the FFmpeg one.

**Voiceover** is out of MVP scope, but leave a `narration` field on the scene DTO and an `ITextToSpeechClient` interface unimplemented. Adding audio later is a filtergraph change, not an architecture change.

---

## 18. AI provider abstraction

Your list of five interfaces is one too many in one place and missing a layer in another. Two corrections:

> **Collapse vision into the chat client.** `IVisionModelProvider` should not exist. Every serious provider takes images as content parts in the same messages API as text; a separate interface duplicates the retry, cost, tracing and structured-output machinery for no benefit. One `ILanguageModelClient` that accepts multimodal content parts covers strategist, copywriter, marketing QA *and* visual QA.

> **Do not define `IVideoGenerationProvider` yet.** You have no implementation, no requirement, and no idea what the API shape will be. An interface written for an imagined future is worse than no interface, because the first real implementation will not fit it and you will keep the bad shape out of loyalty. Delete it until you need it.

### What to build

Build on `Microsoft.Extensions.AI`'s `IChatClient` rather than inventing your own message model — it already gives you multimodal content parts, tool definitions, structured output, streaming, and a middleware pipeline, with adapters for the major providers. Your own abstraction is then a thin domain layer on top:

```csharp
ILanguageModelClient                    // wraps IChatClient
   Task<LlmResult<T>> CompleteAsync<T>(
        ModelProfile profile,           // named, config-resolved
        PromptRendering prompt,         // system + user + images
        JsonSchema schema,              // enforced structured output
        LlmCallOptions options, CancellationToken ct);
   // LlmResult<T>: Value, RawJson, Usage(in/out/cached), FinishReason,
   //               ModelId, ProviderRequestId, Latency

IImageGenerationClient
   Task<GeneratedImage> GenerateAsync(
        ImageModelProfile profile, string prompt,
        ImageSize size, int? seed, CancellationToken ct);

ITextToSpeechClient    // declared, unimplemented, MVP-excluded
```

#### Model profiles, not model names

No agent ever names a model. It names a *profile* — `strategist`, `copywriter`, `visual-qa`, `background-image` — resolved from configuration to a provider, model ID, temperature, max tokens, and price. Swapping a model, or A/B testing one, becomes a config change plus a row in the price table. This single indirection is most of what "provider independence" actually means in practice.

#### The middleware pipeline

Everything cross-cutting is a decorator around the client, in order: *Tracing → CostAccounting → BudgetGuard → Retry/Backoff → RateLimit → Cassette (test only) → Provider*. Agents stay ignorant of all of it, and the cassette layer — record/replay of provider traffic keyed by request hash — is what makes deterministic CI possible (§27).

> **Where to stop abstracting.** Do not abstract prompt *caching*, tool-use semantics, or reasoning parameters behind a common interface — providers differ too much and you will build a lossy lowest common denominator. Expose them as a provider-specific options bag on `LlmCallOptions`, and accept that switching providers means revisiting those settings. That is honest, and it is a day's work when it happens.

---

## 19. Prompt management and versioning

Prompts are source code and deserve source-code treatment: reviewed in PRs, diffable, versioned, and never edited in production by accident.

```
Application/Prompts/
   content-strategist/v3.prompt.md
   copywriter/v5.prompt.md
   creative-director/v2.prompt.md
   visual-qa/v4.prompt.md
   marketing-qa/v2.prompt.md
   _schemas/ContentStrategy.schema.json ...

--- v3.prompt.md frontmatter ---
id: content-strategist
version: 3
modelProfile: strategist
schema: ContentStrategy
temperature: 0.8
maxOutputTokens: 4000
variables: [brandBlock, audienceBlock, historyDigest, quotas, goals]
description: Plans one week of content items.
---
<system> ... </system>
<user>   ... {{ historyDigest }} ... </user>
```

- **Embedded resources**, loaded and hashed at startup; each is upserted into `PromptVersion`. `AgentRun.PromptVersionId` is a foreign key, so every output is traceable to exact prompt text forever.
- **Immutable.** Editing a prompt means writing `v4.prompt.md`. A startup check fails the app if the hash of an existing version changed — that catches an accidental edit to history.
- **Templating:** Scriban or Handlebars.NET with strict mode. A test asserts that the declared `variables` list exactly matches the placeholders used and the properties the calling agent supplies. Missing-variable bugs in prompts are invisible at runtime and expensive.
- **Tenant untrusted data is delimited**, never interpolated raw into instructions — see §25.
- **Selection:** `IPromptSelector` resolves (promptId, tenant, experiment) to a version. MVP returns the configured latest. A/B later: assign a variant per *campaign* (sticky, so a week is internally consistent), record the variant on every AgentRun, and compare QA pass rate, attempts per item, cost per item, and human approval rate.

---

## 20. Background job architecture

```
Postgres table:  jobs(id, tenantId, type, payloadJson, runAt, state,
                      attempts, leaseUntil, lockedBy, lastError,
                      idempotencyKey UNIQUE, priority)

Worker loop (N concurrent consumers per process):
  BEGIN;
    SELECT * FROM jobs
     WHERE state='Pending' AND runAt <= now()
     ORDER BY priority, runAt
     FOR UPDATE SKIP LOCKED LIMIT 1;
    UPDATE ... SET state='Leased', leaseUntil=now()+interval '5 min',
                   lockedBy=:workerId;
  COMMIT;
  -- execute handler --
  -- result + next job + domain writes committed TOGETHER --

Reaper: leased jobs past leaseUntil -> Pending, attempts+1
        attempts > max -> Dead (visible in an admin view)
Heartbeat: long renders extend their own lease
```

Why not Hangfire for the queue itself: the killer feature here is committing the job outcome, the workflow transition, the AgentRun and the CostEntry in one transaction. With an external queue you get dual-write and eventually a campaign that "finished" without a plan. Hangfire still earns its place as the **cron scheduler** and dashboard — its recurring jobs simply enqueue into your table.

Queue separation is by job type on a single table, with separate consumer pools: `orchestration` (fast, high concurrency), `render` (slow, concurrency capped at renderer capacity), `packaging` (rare, long). Priority ensures a manual "Generate now" jumps ahead of a scheduled batch. Cancellation is a flag on the workflow run, checked at every step boundary — not thread aborts.

---

## 21. Manual, scheduled, and event triggers

All three converge on one use case before any workflow logic exists. That is the whole design.

```
POST /api/brands/{id}/campaigns:generate  (manual)
Hangfire cron "0 6 * * MON" per brand timezone (scheduled)
Future: ProductReleaseCreated domain event   (event)
                    |
                    v
        StartCampaignCommand {
            brandId, weekStart, triggerType,
            overrides?  (topic hint, extra items, budget override),
            idempotencyKey = hash(brandId, weekStart, triggerType)
        }
                    v
        CampaignStarter (single implementation)
          - resolve tenant + brand
          - snapshot Brand Brain          -> BrandProfileVersion
          - resolve quotas + budget
          - create ContentCampaign (Draft) - unique constraint absorbs
            double fires
          - enqueue campaign workflow job
```

Scheduling notes worth deciding once: the cron runs per brand in the brand's own timezone (store an IANA ID, use `TimeZoneInfo` with the tz database, and let Hangfire fire hourly while a guard checks "is it 06:00 Monday *there*" — simpler and more robust than per-tenant cron expressions). A missed window because the worker was down is caught by a daily reconciliation job: "for each active brand, does a campaign exist for the current week? If not, start one." That reconciler is also your manual disaster recovery.

The event trigger needs no new pipeline: a `ProductRelease` record becomes an override on `StartCampaignCommand` that pins one or two items to a launch topic and injects the release notes as a temporary high-priority ProductFact set. Build the override hook now (it costs nothing); build the event source later.

---

## 22. Campaign output and packaging

Your proposed layout is good. Two additions make it durable.

```
campaigns/{campaignId}/
  plan.json            campaign theme, items, publish days, status per item
  manifest.json        machine-readable index: every file, sha256, bytes,
                       mime, itemId, role - the ZIP and UI both build
                       from this, never from a directory listing
  post-01/
    image.png          1080x1350
    caption.txt        ready to paste, with hashtags at the end
    metadata.json      hook, headline, cta, hashtags[], topic, pillar,
                       templateId, publishDay, factCitations[],
                       qualityScore, attempts, needsReview
  post-02/
    slide-01..05.png
    caption.txt  metadata.json
  reel-01/
    reel.mp4  cover.png  caption.txt  script.txt  metadata.json
  package.zip          built on demand, cached, invalidated by manifest hash
```

- **Numbering** is assigned at packaging time by publish day then creation order, so `post-01` is stable across ZIP downloads and email links.
- **The ZIP is built by streaming** from object storage to the response (or to a stored object for large packages) — never buffered in memory. Cache it keyed by manifest hash; if an item is approved later, the hash changes and the ZIP rebuilds.
- **Items needing review are included** in the package, in a `_needs-review/` subfolder with their findings in `metadata.json`. Do not hide work from the user.
- **The email** is short: theme, a thumbnail grid of what is ready, a count of what needs review, and one signed link to the campaign in the app (not to raw storage, so the link keeps working and access stays authorised). Send it once, from the packaging step, recorded on `CampaignPackage.EmailSentAt` so a retry cannot double-send.

---

## 23. Observability and tracing

Two layers, because they answer different questions and neither substitutes for the other.

### Layer 1 — relational, queryable, permanent

The `WorkflowRun` / `WorkflowStep` / `AgentRun` / `QualityReview` / `CostEntry` tables are product data, not telemetry. They answer "what did this campaign cost", "which prompt version produced this caption", "what is our QA pass rate this month", and they survive trace retention. They are also what the UI renders on a campaign's detail page — showing the user the run tree is a genuine feature, not just an internal tool.

### Layer 2 — OpenTelemetry, for latency and debugging

```
Trace: campaign.generate                         (root, trace-id = per run)
 |- workflow.campaign     campaign.id, brand.id, trigger
 |   |- agent.content-strategist                 gen_ai.* attributes
 |   |   +- gen_ai.chat  model, tokens.in/out, cost.cents, prompt.version
 |   |- workflow.item  x N                       item.id, content.type
 |   |   |- agent.creative-director
 |   |   |- agent.copywriter        attempt=1
 |   |   |- render.image            template.id, duration, bytes
 |   |   |- qa.deterministic        findings=2, ssim=0.981
 |   |   |- agent.visual-qa         findings=1, severity=major
 |   |   |- remediation             action=RewriteSlot, target=headline
 |   |   |- agent.copywriter        attempt=2
 |   |   +- render.image            attempt=2 -> approved
 |   +- workflow.packaging          files=14, zip.bytes=18.2MB
```

Use the OTel `gen_ai.*` semantic conventions rather than inventing attribute names. Baggage carries `tenant.id`, `campaign.id`, `workflow_run.id` across the process boundary into the Renderer, so a slow render shows up inside the right campaign trace. Correlate the two layers by writing the `TraceId` and `SpanId` onto every `AgentRun` and `WorkflowStep` row — that is the join that makes both layers useful.

**Payloads go to object storage, not the database and not the logs.** Full prompt inputs and raw model outputs under `runs/{workflowRunId}/agent/{agentRunId}/`, referenced from the AgentRun row. Logs get IDs and metrics only. Structured logging with Serilog, JSON to stdout, correlation IDs enriched from the ambient activity.

### Metrics worth a dashboard from week one

- First-attempt QA pass rate, by content type and by template — your single best quality signal.
- Attempts per approved item; items ending in `NeedsHumanReview` per campaign.
- Cost per item and per campaign, split by agent and by provider.
- Render p50/p95 duration and failure rate; browser pool saturation.
- Time from trigger to `Ready`.
- Human approval rate on `NeedsHumanReview` items — if you approve most of them unchanged, your QA is too strict.

Locally, the .NET Aspire dashboard as an OTLP sink gives you all of this for one container and no configuration. In production, Seq or Grafana + Tempo. Do not build a custom trace viewer.

---

## 24. Cost tracking and hard limits

### Accounting

A `PriceBook` in configuration maps (provider, model, unit kind) to a price with an effective date range. Every provider call returns usage; the CostAccounting middleware converts usage to micro-cents using the price row in force *at call time*, and writes a `CostEntry` with the unit price snapshotted onto the row. Snapshotting matters: when prices change, history must not silently rewrite itself. Non-token costs (image generations per image, renderer seconds if you ever bill them) are entries in the same ledger with a different `Kind`.

### Enforcement — reserve, then commit

```
Before any billable call:
   estimate  = profile.EstimateCents(promptTokens, maxOutputTokens)
   spent     = SUM(CostEntry) for item and for campaign  (indexed)
   reserved  = outstanding reservations for this scope
   if spent + reserved + estimate > limit  ->  BudgetExceeded
After the call:
   release the reservation, write the actual CostEntry
```

Reservations exist because parallel items would otherwise each see the same "spent" figure and collectively blow the campaign budget. Keep them in a small table with a short TTL, cleaned by the same reaper as job leases.

| Limit | Default | On breach |
|---|---|---|
| Quality attempts per item | 3 | Item → NeedsHumanReview, best attempt promoted |
| Cost per content item | $0.60 | Item → NeedsHumanReview |
| Cost per campaign | $6.00 | Remaining items → NeedsHumanReview; campaign → PartiallyReady |
| Generated image variants per item | 3 | Reuse best background, stop generating |
| Reel render attempts | 2 | Fall back to SafeMode reel template, then review |
| Steps per item / wall clock per run | 40 / 45 min | Force-terminate to NeedsHumanReview |
| Tenant monthly spend | configurable | Block new campaigns, alert; never truncate a running one |

All limits live on the Tenant with per-campaign overrides, so tuning is data, not a deploy. Every breach writes a `ContentRevision` explaining which limit stopped the work — when you are debugging "why is this post mediocre", knowing it hit a budget wall on attempt two is the whole answer.

> **Calibrate the defaults early.** Those dollar figures are placeholders. In Phase 3, run ten real campaigns with limits set high and no enforcement, then set each limit at roughly the 90th percentile of observed cost. A budget guard tuned by guesswork will either never fire or will strangle normal operation, and both failure modes look like bugs.

---

## 25. Security

| Concern | Approach |
|---|---|
| **Tenant isolation** | `TenantId` on every row, EF global query filters driven by an `ITenantContext` resolved once per request/job, an architecture test asserting every `ITenantOwned` entity has a filter, and an integration test that seeds two tenants and asserts every read endpoint returns 404 across the boundary. Add Postgres RLS when you take a second real customer. |
| **Upload validation** | Magic-byte sniffing (never trust the extension or content-type), an allow-list of image/video types, dimension and byte-size caps, and **re-encode every raster image on ingest** through Magick.NET. Re-encoding strips EXIF, GPS, embedded scripts and polyglot payloads in one step and normalises the colour profile, which you want anyway. |
| **SVG uploads** | Treat as hostile. An SVG logo is XML that can carry scripts and external references, and it will be loaded into your Chromium renderer. Either rasterise to PNG at several sizes on ingest and discard the SVG, or sanitise through a strict allow-list. Rasterising is the right MVP call. |
| **Renderer isolation** | The renderer executes attacker-influenceable HTML. Run it as a non-root user in its own container, no cloud metadata access, egress blocked at the network level, all page requests aborted at the Playwright route level, a hard per-render timeout, and memory limits with browser recycling. Never render a URL supplied by a user. |
| **Object storage** | Private buckets, no public read ever. Presigned GET URLs minted by the API after authorisation, 15-minute TTL. Uploads go through the API (validated, re-encoded) rather than presigned PUT, at least until upload volume justifies otherwise. |
| **Prompt injection** | Today the risk is low (you write the brand data); the moment you ingest websites in onboarding it becomes real. Design for it now: all tenant-supplied and scraped text is wrapped in explicit delimiters and labelled untrusted data, never concatenated into the instruction section; agents have no tools and no write access, so a successful injection can only produce bad content, not bad actions; and every output passes the deterministic validators — asset IDs must exist and belong to the tenant, claims must cite real ProductFacts, template IDs must be in the candidate set. Structural validation is a far better defence than instructing the model to ignore instructions. |
| **Website ingestion (later)** | Fetch server-side with SSRF protection (resolve DNS, reject private ranges and redirects into them), strip scripts, cap size, and treat everything extracted as an *unverified suggestion* the user must confirm before it becomes a ProductFact. Never let scraped text become a citable fact automatically. |
| **Secrets** | Never in appsettings or the repo. User Secrets locally, environment variables from a secret store in production, and a startup validation that fails fast on missing keys. Redact API keys from logs and from the archived LLM payloads. |
| **Generated files** | Content-typed on upload, served with `Content-Disposition: attachment` and `X-Content-Type-Options: nosniff`, from a storage domain distinct from the app domain. |
| **Retention** | Per-tenant policy: failed attempt artefacts 30 days, LLM payload archive 90 days, approved campaign assets indefinite until deletion. A nightly job enforces it. Tenant deletion removes the storage prefix and cascades the rows — write that job in the MVP while the schema is small, because it never gets easier. |
| **Auth** | MVP: a single admin user, ASP.NET Core Identity or a hosted IdP with cookie auth. Do not build roles, invitations, or org management yet — but do put `TenantId` in the auth claims so the tenant context has one obvious source. |

---

## 26. Testing strategy

| Layer | Scope | Runs |
|---|---|---|
| **1. Unit** | State machine transitions (including every illegal one), RemediationRouter mapping, SimHash/novelty rules, budget arithmetic, contrast and overflow calculators, manifest validation, prompt variable binding, cost conversion. | Every commit, seconds |
| **2. Architecture** | The dependency rules in §11. Cheap and they protect the central invariant. | Every commit |
| **3. Integration** | Testcontainers Postgres + MinIO. EF mappings and migrations, tenant query filters, job leasing under concurrency, presigned URL round trip, ZIP packaging, retention job. | Every commit, ~2 min |
| **4. Workflow** | Full campaign end to end with faked providers and a stub renderer. Scripted scenarios: happy path; QA fails once then passes; QA fails three times → NeedsHumanReview; budget exceeded mid-campaign; provider 429 then success; worker crash mid-run then resume; duplicate trigger is idempotent. | Every commit, ~1 min |
| **5. Renderer** | Golden images. Render each template with fixed fixture specs, compare to a committed baseline at SSIM ≥ 0.995. **Must run inside the production renderer image** — a different Chromium or font set makes them meaningless. Plus the calibration corpus from §10 asserting the fidelity thresholds still separate good from mutated. | PR + nightly, ~3 min |
| **6. Agent evals** | §27. | Cassette mode on PR; live mode nightly |

### Golden test data

`tests/ContentPilot.GoldenData/` is a committed fixture tenant: the Appointso brand profile as JSON, six real screenshots, a logo in three variants, twenty product facts, two personas, and twelve weeks of synthetic content history designed to trip the novelty rules. Everything is seeded by a single `GoldenTenantSeeder` shared by integration, workflow and eval suites, so a fixture change propagates everywhere at once. Keep the binary assets small (under about 5 MB total) or use Git LFS. Baseline images live beside the template they test, and updating one is a deliberate PR with visual diffs attached — never an automatic `--update-snapshots` in CI.

---

## 27. AI eval strategy

Evals are not tests, and conflating them is how teams end up with a red CI they learn to ignore. Tests are deterministic and block merges. Evals are statistical, produce a score, and block merges only against a *threshold you set from a baseline*.

### Two execution modes

- **Cassette mode (CI, every PR).** Recorded provider responses keyed by a hash of (prompt version, model profile, rendered input). Deterministic, free, fast. It cannot tell you whether a new prompt is *better*, but it catches contract breaks, schema drift, and validator regressions instantly. A missing cassette fails the build rather than silently calling the API.
- **Live mode (nightly + before any prompt version bump).** Real providers, N=5 runs per scenario for variance, results written to an `EvalRun` table and rendered as a trend page. Compare against the previous prompt version, not against an absolute ideal.

### Scenario catalogue

| Scenario | Assertion | Kind |
|---|---|---|
| Strategist avoids recent topics | No proposed topic within Hamming 6 of the 12-week seeded history | Deterministic |
| Strategist respects quotas and exclusions | Exact type counts; zero excluded topics | Deterministic |
| Copywriter invents no features | Every claim cites a ProductFact ID; an LLM judge confirms the cited fact supports the claim; a planted trap fact ("group bookings") absent from the brand must never appear | Mixed |
| Copywriter respects slot budgets | Every slot within manifest limits, zero tolerance | Deterministic |
| CreativeDirector picks a legal template and asset | Template in candidate set; assets exist, correct kind, correct tenant | Deterministic |
| VisualQA catches a mutated screenshot | 10 deliberately corrupted renders (overlay, blur, squash, scrim, wrong asset) → recall ≥ 0.9 for `Screenshot*` findings | Deterministic labels |
| VisualQA catches a distorted logo | Non-uniform scale, wrong colour, insufficient clear space → recall ≥ 0.9 | Deterministic labels |
| **VisualQA does not cry wolf** | 15 known-good renders → false-positive rate ≤ 0.15. The single most valuable eval you will write. | Deterministic labels |
| Retry terminates | Renderer stubbed to always fail QA → exactly 3 attempts, terminal NeedsHumanReview, cost below cap | Deterministic |
| Content history respected end to end | Two consecutive campaigns → zero topic or hook collisions between them | Deterministic |
| Overall campaign quality | Rubric-scored by an LLM judge on brand fit, hook strength, specificity, CTA clarity — plus **your own 1–5 rating** recorded in the eval table | Judge + human |

> **The eval that actually decides the product.** Notice how many of these are deterministic — that is deliberate, because LLM judges are noisy and expensive. But the one that matters most cannot be automated: **every week, rate each generated item 1–5 on "would I publish this?" and store it.** Fifty of those ratings is a better product signal than any rubric, and it is the dataset that tells you which template, pillar and prompt version to invest in. Build the one-click rating UI in Phase 8; it takes an hour and it is the highest-leverage screen in the application.

---

## 28. Docker and deployment for the MVP

```
docker-compose.yml
  api        ContentPilot.Api        aspnet:9  (~110 MB)
  worker     ContentPilot.Worker     aspnet:9  (same image, different entry)
  renderer   ContentPilot.Renderer   mcr.microsoft.com/playwright/dotnet
                                     + ffmpeg + fontconfig  (~1.8 GB)
  postgres   postgres:17-alpine      volume
  minio      minio/minio             volume        (prod: Cloudflare R2)
  mailhog    (dev only)                            (prod: Resend/SES)
  aspire     OTLP dashboard (dev)                  (prod: Seq or Tempo)
```

- **Api and Worker share one image** with different entrypoints — same code, no drift, half the build time. The Renderer is separate purely because of its base image size.
- **Fonts are baked into the renderer image** and the image is pinned by digest. Also install the brand's fonts and a broad Unicode fallback set; a missing glyph renders as tofu and QA will not always notice.
- **Migrations** run as an explicit init step (a `--migrate` entrypoint), not automatically on API startup — automatic migration with multiple replicas is a race.
- **Renderer capacity** is the scaling knob: it is CPU and memory hungry. Give it 2–4 vCPU and 4 GB, cap concurrent renders with a semaphore, and let the render job queue back up rather than thrashing.
- **Production MVP** is one VM (Hetzner or similar, 4 vCPU / 16 GB) running compose, plus managed Postgres if you would rather not own backups, plus R2. That is genuinely enough for a single tenant producing eight items a week, and it costs less per month than one hour of engineering time spent on Kubernetes.
- **Backups:** nightly `pg_dump` to R2 with a restore drill actually performed once. Object storage is your other source of truth — enable versioning on the bucket.
- **CI:** GitHub Actions — build, unit + architecture + integration + workflow tests, build both images, run renderer golden tests inside the renderer image, run cassette evals, push by digest. Nightly: live evals and a real campaign generation against the golden tenant.

---

## 29. Configuration and secrets

- **Strongly typed options** with `IOptions<T>`, `ValidateDataAnnotations()` and `ValidateOnStart()`. A missing or malformed setting must crash at boot, not at 06:00 on Monday inside a workflow.
- **Layering:** `appsettings.json` (structure and non-secret defaults) → `appsettings.{Env}.json` → environment variables (secrets and per-environment values) → User Secrets locally. No secret is ever committed, including in compose files — use a git-ignored `.env`.
- **Three distinct config domains**, kept separate so ownership is clear: *infrastructure* (connection strings, bucket, SMTP), *AI* (model profiles, price book, provider keys), *policy* (default budgets, attempt limits, QA thresholds, retention days).
- **Policy config belongs in the database**, not in files, once it is per-tenant — with file values as the fallback default. Tuning a QA threshold should not require a deploy.
- **Feature flags** as simple config booleans: `Reels.Enabled`, `VisualQa.Enabled`, `Email.Enabled`, `BackgroundGeneration.Enabled`. During early phases you will want to run the pipeline with pieces switched off, and this is far better than commenting out orchestrator steps.

---

## 30–31. MVP implementation phases

Ten phases. Each is independently demoable and leaves the system in a working state. Complexity is relative effort, not calendar time.

### Phase 0 — Foundations and a walking skeleton · **Medium**

**Objective:** a solution that builds, runs in compose, stores a row, stores a file, and executes one trivial job end to end. No AI.

**Features**
- Solution and project skeleton per §11 with architecture tests enforcing the dependency rules from day one
- EF Core + Postgres, UUIDv7, tenant context and global query filters, migration entrypoint
- `IObjectStore` over MinIO with presigned URLs
- Postgres job queue with leasing, reaper, idempotency; one `PingJob`
- Serilog + OpenTelemetry wired to the Aspire dashboard
- docker-compose with api, worker, postgres, minio, aspire

**Database:** initial migration — `Tenant`, `Brand`, `Job`, plus the tenancy base configuration

**Tests:** architecture rules; Testcontainers integration for EF, storage round trip, and concurrent job leasing (two workers, one job, exactly one execution)

**Acceptance:** `docker compose up` yields a healthy stack; a queued PingJob is executed exactly once by one of two workers and its trace appears in Aspire; a cross-tenant read returns nothing.

---

### Phase 1 — Brand Brain and asset library · **Medium**

**Objective:** Appointso fully represented in the system, with a deterministic, budgeted brand block ready to inject into prompts.

**Features**
- Brand profile CRUD (API only; a minimal HTML admin page or Swagger is fine)
- Asset upload: magic-byte validation, re-encode via Magick.NET, SVG rasterisation, pHash and dominant colours, content-addressed storage, derived thumbnails
- ProductFact, AudiencePersona, ContentPreferences management
- `BrandBrainAssembler` → `BrandSnapshot` → hashed `BrandProfileVersion`
- `BrandBlockRenderer` → compact Markdown for prompts
- `GoldenTenantSeeder` with the real Appointso data

**Database:** `BrandProfile`, `BrandProfileVersion`, `BrandAsset`, `ProductFact`, `AudiencePersona`, `ContentPreferences`, `IndustryProfile`

**Tests:** snapshot determinism (same input → same hash); brand block under 1,200 tokens; upload rejects a renamed executable, a zip bomb, and a scripted SVG; re-encode strips EXIF

**Acceptance:** seeding the golden tenant produces a snapshot whose brand block reads as a coherent brief a human could work from.

---

### Phase 2 — Renderer service and the first static templates · **Large**

**Objective:** hand the renderer a hand-written CreativeSpec and get back an image you would actually post. This phase decides whether the product is good.

**Features**
- Renderer ASP.NET service on the Playwright base image; browser pool; hard timeouts; recycling
- Razor template engine with brand tokens as CSS custom properties; embedded fonts; network aborted
- `TemplateManifest` schema and loader; `TemplateVersion` registration at startup
- Five static templates including one `SafeMode`: hook-overlay, phone-floating, feature-highlight, testimonial, safe-mode
- Render report collection (bounding boxes, overflow, computed colours, occlusion)
- Mask render mode; `/compare` endpoint with Magick.NET (SSIM, ΔE2000, pHash)
- `Rendering.Contracts` DTOs and the typed HTTP client in Infrastructure

**Database:** `TemplateVersion`; `ContentAsset` (used standalone for now)

**Tests:** golden images per template inside the renderer image at SSIM ≥ 0.995; render report accuracy against deliberately overflowing fixtures; determinism (same spec rendered 10× is byte-identical); concurrency and memory soak

**Acceptance:** five templates render a real Appointso screenshot at 1080×1350 in under two seconds each, and **you would publish at least three of them unedited**. If not, iterate here before proceeding — nothing downstream can rescue a weak template.

---

### Phase 3 — The text agents and the LLM layer · **Large**

**Objective:** Brand Brain in, validated ContentStrategy and CopySet out, with full run and cost accounting.

**Features**
- `ILanguageModelClient` over `Microsoft.Extensions.AI`; model profiles; the middleware pipeline (tracing, cost, budget, retry, rate limit, cassette)
- Prompt loader, `PromptVersion` registration, Scriban rendering with strict variable checking
- `LlmAgentBase`, `AgentRun` recording, payload archiving to storage
- ContentStrategistAgent + validators; ContentMemory digest and SimHash novelty validator
- CreativeDirectorAgent pass A: deterministic `TemplateSelector` + LLM ranking + `CopyBrief`
- CopywriterAgent writing into slots with budgets and fact citation
- `CreativeSpecAssembler` pass B and the deterministic `SpecValidator`

**Database:** `PromptVersion`, `AgentRun`, `CostEntry`, `ContentCampaign`, `ContentPlan`, `ContentItem`, `CreativeSpec`, `ContentHistoryEntry`

**Tests:** every validator unit-tested against hand-written bad outputs; cassette-mode evals for quota, novelty, citation and budget compliance; cost arithmetic against recorded usage

**Acceptance:** a CLI command produces a validated week of specs for the golden tenant, every spec passes the validator, and every AgentRun carries prompt version, model, tokens and cost.

---

### Phase 4 — Deterministic QA and fidelity calibration · **Medium**

**Objective:** catch most defects without spending a token, and know your thresholds are real.

**Features**
- `DeterministicQaSuite`: overflow, safe areas, contrast, logo integrity, file sanity
- Screenshot fidelity checker per §10 (occlusion, aspect, pHash, MS-SSIM, ΔE2000)
- The closed `QaFindingCode` enum and the `QualityReview` record
- **Calibration harness:** 20 screenshots × 5 templates × 3 scales, plus injected mutations; produces the threshold report

**Database:** `QualityReview`

**Tests:** the mutation corpus — every injected defect is detected, every clean render passes; thresholds asserted against the calibration distributions so a future renderer change that breaks fidelity fails CI

**Acceptance:** zero false positives on 15 known-good renders; 100% detection of overlay, blur, squash, scrim and wrong-asset mutations; the report documents the chosen thresholds and why.

---

### Phase 5 — Orchestrator, retries and self-correction · **Large**

**Objective:** one trigger produces a complete week of static images, correcting itself and stopping when it should.

**Features**
- `CampaignWorkflow` and `ContentItemWorkflow` state machines; step persistence; leasing; resumability; cancellation
- `RemediationRouter` and the escalation ladder; the three attempt counters
- `BudgetGuard` with reserve/commit; all hard limits from §24
- Background image generation with seeds and variant caps
- Manual trigger endpoint and the Hangfire weekly cron with the timezone guard and reconciler

**Database:** `WorkflowRun`, `WorkflowStep`, `ContentRevision`, `BudgetReservation`

**Tests:** the full workflow scenario matrix from §26 row 4, plus: kill the worker mid-render and assert the run resumes with no duplicate spend; assert an item that fails QA three times ends in NeedsHumanReview with its best attempt promoted

**Acceptance:** "Generate week" produces four to five approved static images unattended, with a complete run tree and a campaign cost under the configured cap; forcing QA failure terminates in exactly three attempts.

---

### Phase 6 — Visual QA and Marketing QA · **Medium**

**Objective:** add judgement on top of measurement, without turning QA into a cost sink.

**Features**
- VisualQaAgent (full image + 150 px thumbnail, closed finding enum, confidence threshold)
- MarketingQaAgent with claim-grounding verification against cited ProductFacts
- Parallel gate execution and finding merge; QA pass-rate metric
- Carousel continuity checks

**Database:** no new tables; finding payloads extend `QualityReview`

**Tests:** the four QA eval scenarios from §27, including the false-positive rate on known-good renders — treat > 0.15 as a blocking regression

**Acceptance:** the QA agents catch every planted defect in the eval corpus while first-attempt pass rate on clean generation stays above 60%.

---

### Phase 7 — Reels · **Large**

**Objective:** one reel per week that you would post, produced by the same machinery.

**Features**
- ReelSpec and scene DTOs; reel template manifests with per-scene slot budgets
- Layer rendering through the existing Playwright pipeline
- `SceneComposer` and the FFmpeg filtergraph builder; music bed support
- Three reel templates: problem-solution, feature-tour, before-after; one in SafeMode
- Cover frame extraction; keyframe QA; container-level deterministic checks

**Database:** no new tables; `ContentAsset.Kind` gains video and cover

**Tests:** golden video checks on duration, resolution, fps, frame hashes at fixed timestamps; keyframe QA integration; render under 10 seconds for a 15-second reel

**Acceptance:** a 15-second 1080×1920 reel with real screenshots, readable text and clean transitions renders reliably, and you would publish it.

---

### Phase 8 — Packaging, delivery and human review · **Medium**

**Objective:** the weekly product experience — open a link, see the week, download it, rate it.

**Features**
- Packaging step: `plan.json`, `manifest.json`, folder layout, stable numbering
- Streaming ZIP with manifest-hash caching
- Campaign browse and item detail API; presigned download URLs
- Review UI: approve, reject, view findings, view the run tree, and the **1–5 "would I publish this" rating**
- Weekly email with thumbnail grid and a signed app link; once-only send
- Retention and tenant-deletion jobs

**Database:** `CampaignPackage`, `HumanRating`

**Tests:** package structure matches the manifest exactly; ZIP streams without buffering a large campaign; email sends once across retries; approving a reviewed item rebuilds the package and writes a ContentHistoryEntry

**Acceptance:** Monday morning — an email arrives, the link opens the week, every asset downloads individually and as a ZIP, and each item can be rated in one click.

---

### Phase 9 — Hardening: observability, cost calibration, evals · **Medium**

**Objective:** make the system explainable and its limits real rather than guessed.

**Features**
- The metric dashboard from §23; alerting on campaign failure and budget breach
- Budget defaults recalibrated from ten real campaigns
- The full eval suite in both modes; nightly live run; `EvalRun` trend page
- Admin views: dead jobs, stuck runs, manual step advance, campaign re-run
- Backup and restore drill actually performed

**Database:** `EvalRun`, `EvalResult`

**Acceptance:** any campaign can be explained end to end from the UI; every limit is set from observed data; nightly evals produce a trend you would trust before changing a prompt.

---

### Phase 10 — Post-MVP options, in priority order · **Medium each**

**Objective:** only start these once four consecutive weeks have shipped content you actually published.

- Angular UI proper, replacing the minimal review pages
- Website ingestion and guided onboarding (with the SSRF and injection controls from §25)
- Multi-tenant signup, auth, roles, Postgres RLS
- Read-only MCP façade over Brand, Content and Assets
- Prompt A/B testing driven by the human ratings
- Instagram and Facebook publishing via the Graph API
- Remotion as an alternative `IReelRenderer`, if motion becomes the differentiator

---

## 32. What should explicitly NOT be built yet

- **MCP servers of any kind.** §4.
- **A vector database or embeddings.** §14.
- **Temporal, Dapr, Elsa, MassTransit, or any workflow/message framework.** Ten states and one fan-out.
- **Kubernetes.** One VM and compose.
- **Remotion.** §17.
- **Generative video, voiceover, music generation.** Declare the interfaces; implement nothing.
- **Social platform APIs, analytics, engagement feedback loops.** Explicitly out.
- **Billing, signup, org management, roles, invitations.** One user.
- **An agent that writes or selects templates.** Templates are design work, and a model producing arbitrary CSS destroys every guarantee in §10 and §16.
- **Agent-to-agent messaging, a planner agent, or a tool-calling loop for orchestration.** This is the thing you specifically said you did not want; it will be proposed again during implementation because it looks elegant. Refuse it.
- **A generic plugin system for templates or providers.** Two providers and eight templates do not need extensibility infrastructure.
- **Caching layers, Redis, read replicas.** You are generating eight items a week.
- **An event-sourced domain.** The append-only run tables give you the audit trail you actually want, at a fraction of the cost.

---

## 33. The biggest technical risks

| # | Risk | Why it is dangerous | Mitigation |
|---|---|---|---|
| 1 | **The output is technically correct and aesthetically mediocre** | This is the risk that kills the product, and no amount of architecture addresses it. "Passes QA" and "I want to publish this" are different bars, and the gap is invisible until you look at real output. | Front-load Phase 2. Gate the whole project on the "would publish three of five" test before building the orchestrator. Hire or borrow a designer for the templates if yours are not strong. |
| 2 | **AI background generation looks generic** | Generic stock-photo-ish backgrounds are the single clearest "this was made by AI" signal, and they will drag every template down. | Prefer brand-coloured gradients, abstract shapes and real photography from the asset library. Treat generated backgrounds as one option among several, heavily constrained by prompt templates per visual style — not as the default. |
| 3 | **QA agents cry wolf** | An over-eager visual QA turns every item into three attempts, triples cost, and lands everything in review. It looks like a quality system while being a cost sink. | The false-positive eval in §27, the pass-rate metric, severity thresholds, and confidence gating. Measure before trusting. |
| 4 | **Text overflow across languages and brands** | Templates tuned for English will break on Croatian or German compounds, and the failure is a clipped headline nobody notices until it is posted. | Slot budgets in the manifest, shrink-to-fit within bounds, render-report overflow detection with zero tolerance, and a golden test per template per supported language. |
| 5 | **Renderer non-determinism** | A Chromium or font update silently changes rasterisation; golden tests fail en masse and you cannot tell a real regression from a rendering shift. | Pin the image by digest, bake fonts, block the network, and treat a baseline update as a deliberate reviewed PR. |
| 6 | **Cost per campaign is unknown** | Vision QA on multiple attempts across eight items adds up fast, and an unpriced retry loop can produce a surprising bill. | Budget guard from Phase 5, not later; measure across ten campaigns before setting caps; alert on any campaign above 2× median. |
| 7 | **Reel quality plateau** | FFmpeg-composited reels may land at "acceptable" rather than "good", and the fix is a different renderer. | Prototype one reel by hand in the spikes before committing (§35). Keep `IReelRenderer` swappable. |
| 8 | **Structured output drift** | Provider or model changes alter schema adherence subtly; validators start failing in production on Monday morning. | Cassette evals on every PR, nightly live evals, and schema-repair retries that log loudly rather than silently succeeding. |
| 9 | **Licensing surprises** | ImageSharp, Remotion and any music bed all have commercial terms that bite exactly when the product succeeds. | Decide before coding (§34). Magick.NET and royalty-free licensed audio. |
| 10 | **Appointso-specific logic leaking into core** | Every shortcut taken for one tenant becomes a rewrite when the second arrives. | The golden tenant is data, not code. Add a second fictional brand in a completely different industry as a test fixture in Phase 3 — it will expose leakage immediately. |

---

## 34. Decisions to make before writing code

1. **Image library and licence:** Magick.NET (recommended) or ImageSharp with a commercial licence budgeted. Affects every imaging component.
2. **Primary LLM provider and the model per profile**, plus whether you want a second provider wired from the start for fallback. Affects cost defaults and the price book.
3. **Image generation provider**, and specifically whether it supports seeds — reproducibility of backgrounds depends on it.
4. **Reel renderer:** FFmpeg composition (recommended) versus Remotion. Decide after the spike, not before, but decide before Phase 7.
5. **Aspect ratios and platforms to support in MVP.** 4:5 and 9:16 only is the right answer; each additional ratio multiplies template work.
6. **Languages.** English only, or English plus Croatian? Multi-language doubles copy budgets, template testing, and font coverage. If Croatian matters, decide now.
7. **Music.** Licensed bed, silence, or none in MVP. Affects reel scope and legal exposure.
8. **Object storage in production:** R2 (recommended) versus S3. Affects cost model, not much code.
9. **Hosting shape:** single VM with compose (recommended) versus a PaaS. Affects the renderer's resource ceiling.
10. **Retention periods** for attempt artefacts and LLM payloads — these drive storage cost and the deletion job.
11. **Does a human approve before "Ready"?** I recommend no: publish the package automatically and let review be optional. Requiring approval turns a "your week is ready" product into a "your week needs your attention" product.

---

## 35. Where a proof of concept is needed first

Four spikes, roughly a week in total, and every one of them can invalidate a large chunk of the plan cheaply. Do them before Phase 1.

| Spike | Question | Effort | Kill criterion |
|---|---|---|---|
| **A. Template quality** | Hand-build one HTML/CSS template with a real Appointso screenshot and screenshot it with Playwright. Would you post it? | 1 day | If the answer is no after a day of design effort, the product thesis needs rethinking before any backend exists. |
| **B. Reel composition** | Hand-render four PNG layer sets and assemble a 15-second reel with a single FFmpeg filtergraph. Does it look designed or does it look like a slideshow? | 2 days | If it looks like a slideshow, budget for Remotion in Phase 7 and re-plan that phase as Large-plus. |
| **C. Structured output reliability** | Run your real CreativeSpec schema against your chosen model 50 times with varied inputs. What is the schema-adherence and validator pass rate? | ½ day | Below ~95% first-pass validity means simplify the schema (fewer nested objects, flatter enums) before building agents around it. |
| **D. Vision QA sensitivity** | Take ten good renders and ten deliberately broken ones and ask a vision model to judge them with your draft prompt. Measure both recall and false positives. | ½ day | If false positives exceed 30% on good renders, plan for a much narrower VisualQA scope — artefacts and logos only, leaving composition to deterministic rules. |
| **E. Screenshot fidelity metrics** | Downscale a screenshot to typical render size and measure SSIM and ΔE for clean versus mutated variants. Do the distributions actually separate? | ½ day | If they overlap, the escalate-to-vision band widens and the mask/occlusion check carries more of the weight — adjust §10 accordingly. |

---

## 36–37. Complexity and recommended order

| Order | Phase | Complexity | Why here |
|---|---|---|---|
| 1 | Spikes A–E | Small | Cheapest possible way to invalidate the riskiest assumptions. |
| 2 | Phase 0 — Foundations | Medium | Everything else needs the queue, storage and tenancy. |
| 3 | Phase 2 — Renderer and templates | **Large** | **Deliberately before the agents.** Output quality is the product risk; prove it with hand-written specs while it is still cheap to change your mind. |
| 4 | Phase 1 — Brand Brain | Medium | Now you know exactly what the renderer and prompts need from it. |
| 5 | Phase 3 — Text agents | **Large** | Specs can now be generated rather than written by hand. |
| 6 | Phase 4 — Deterministic QA | Medium | Before the orchestrator, so remediation has something real to route on. |
| 7 | Phase 5 — Orchestrator | **Large** | All the pieces exist; this wires them into an unattended loop. |
| 8 | Phase 6 — LLM QA | Medium | Added on top of a working loop so you can measure its true cost and value. |
| 9 | Phase 8 — Packaging and review | Medium | **Before reels.** A working weekly static-only product beats a half-built one with video, and the rating data starts accumulating immediately. |
| 10 | Phase 7 — Reels | **Large** | The largest remaining chunk, now added to a product that already works. |
| 11 | Phase 9 — Hardening | Medium | Calibrate limits and evals against real usage data, which only now exists. |
| 12 | Phase 10 — Post-MVP | Medium each | Only after four consecutive published weeks. |

> **The one reordering that matters.** Building the renderer and templates *before* the agents inverts the natural instinct to start with the interesting AI work. Do it anyway. A beautiful template with a hand-written spec is a demo you can judge; a perfect agent graph feeding an ugly template is a month you cannot get back.

---

## 38. Closing four

### Recommended MVP architecture

A .NET modular monolith in three deployables. **Api** and **Worker** share one image over a layered solution (Domain → Application → Infrastructure) with a Postgres-backed job queue whose outcomes commit in the same transaction as domain state. **Renderer** is a separate ASP.NET service on the Playwright image, owning Razor templates, Chromium, FFmpeg and Magick.NET, coupled only through a shared contracts project. A deterministic C# state machine drives a campaign workflow that fans out into independent, attempt-bounded item workflows. Five LLM agents — strategist, creative director, copywriter, visual QA, marketing QA — are stateless schema-constrained functions with no tools, no state access, and no knowledge of each other. QA runs cheapest-first, and remediation is a static mapping from a closed finding enum to the earliest step that could fix it. Product screenshots are composited, never generated, and fidelity is verified by mask-render occlusion plus scale-normalised SSIM and ΔE. Postgres holds relational state and JSONB where shapes are open; object storage holds every artefact and every archived model payload. Everything is tenant-scoped from row one, and the whole thing runs on one VM with docker compose.

### Open questions

1. English only, or English plus Croatian in the MVP? This changes template testing, slot budgets and font coverage more than anything else on this list.
2. Which social formats actually matter to you first — is the reel genuinely week-one scope, or would four strong statics and one carousel prove the thesis faster?
3. Should "Ready" require your approval, or should the package be final and review optional? My recommendation is the latter; it changes the product's promise.
4. How much design capacity do you have for templates? This is the true quality ceiling and it is not an engineering problem.
5. Do you have real prior Appointso content to seed content history and act as a tone reference? Its absence weakens the strategist's first several weeks.
6. What is an acceptable cost per week? €2 and €20 lead to genuinely different QA and retry designs.
7. Are backgrounds mostly generated, mostly brand gradients, or mostly your own photography? This drives the biggest quality variable in the system.
8. Is there any appetite for a human-in-the-loop step between strategy and production — approving topics before images are generated? It would cut cost substantially, at the price of the "it just happens" promise.

### Things I would simplify from your brief

- **Drop MCP from the MVP entirely.** Five MCP servers is a distributed system you do not need to solve an in-process problem.
- **Thirteen projects become eight.** Folders, not csproj files.
- **ImageAgent and ReelAgent are not agents.** Rename them to pipelines and move them out of the agent namespace.
- **Delete `IVideoGenerationProvider` and merge `IVisionModelProvider` into the chat client.** Two of your five AI interfaces should not exist yet.
- **No Remotion in the MVP.** FFmpeg over layers you already render.
- **No vector database, no embeddings.** SimHash and a `WHERE` clause.
- **Skip OCR.** The render report already knows where every character is.
- **One campaign-level and one item-level workflow.** Not a workflow per agent.
- **Start with 4:5 and 9:16 only.** Every extra aspect ratio is a full pass over every template.

### Things I would NOT compromise on

- **The orchestrator is the only thing that decides what happens next.** Enforce it with an architecture test, on day one, forever.
- **No model ever touches product screenshot pixels.** Composite only, immutable slots, verified by construction.
- **Every agent output is schema-constrained and then validated deterministically.** Shape is not truth.
- **Every attempt has a bound, and every loop has a wall clock.** Three independent counters, a step cap, and a deadline. There is no path to an infinite loop.
- **Immutable versioning of prompts, templates and brand snapshots.** Without it you cannot explain, reproduce, or improve any output you have ever produced.
- **Cost is metered per call and enforced before the call.** Reserve, then commit.
- **Tenant scoping from the first migration.** Cheap now, a rewrite later.
- **Deterministic checks before LLM checks, always.** A bounding box beats a vision model at reading a bounding box.
- **Renderer determinism:** pinned Chromium, embedded fonts, no network. Everything downstream assumes it.
- **The weekly human rating.** One click, five values, stored. It is the only measurement that tells you whether any of this is working.

---

*Companion web version: https://claude.ai/code/artifact/6c51be16-7ac9-452c-9047-ca22224139af*
