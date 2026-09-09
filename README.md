# ContentPilot

An autonomous content production platform: a business's Brand Brain in, a week of
publishable social content out. Built as a deterministic pipeline with a few narrow LLM
agents in it — not as an agent system with a rendering step.

The full technical plan lives in **[IMPLEMENTATION-PLAN.md](IMPLEMENTATION-PLAN.md)**.
This README covers only how to run what exists today.

Working alongside another agent? Read **[PARALLEL-WORK.md](PARALLEL-WORK.md)** first — it
names the phases that can run side by side and the exact files where two agents collide.

## Status

| Phase | Scope | State |
|---|---|---|
| 0 | Foundations: solution, tenancy, transactional job queue, object storage, telemetry | **Done** |
| 2 | Renderer service, five static templates, screenshot fidelity checking | **Done** |
| 1 | Brand Brain: profile, product facts, personas, quotas, asset library | **Done** |
| 3 | Text agents and the LLM layer | Next |
| 4–9 | QA, orchestrator, reels, packaging, hardening | Not started |

Phase 2 was built before Phase 1 on purpose. Output quality is the product risk, and it is
cheapest to disprove with a hand-written spec before anything depends on it.

There is still no AI in the system. What exists is everything the agents will stand on: a
renderer that composites real product screenshots into designed templates and proves they
survived untouched, and a Brand Brain that turns a business into the exact prompt block
those agents will read.

## Requirements

- .NET SDK 10.0.303 or later (pinned in `global.json`)
- Docker (for the local stack and the container-backed tests)
- `dotnet-ef` tool, for creating migrations
- Chromium for Playwright, installed **inside the repository** so nothing lands outside it:

```bash
dotnet build src/ContentPilot.Renderer
PLAYWRIGHT_BROWSERS_PATH=./.playwright \
  ./src/ContentPilot.Renderer/bin/Debug/net10.0/playwright.ps1 install chromium
```

> **Disk space.** The renderer image is roughly 2.7 GB (Chromium plus FFmpeg) and Docker's
> build cache adds several more on top. Check for ~10 GB free before `docker compose build`,
> and run `docker builder prune -af` afterwards if space is tight.

## Run the stack

```bash
cp .env.example .env          # optional; defaults work for local development
docker compose up --build
```

| Service | URL |
|---|---|
| API | http://localhost:8080 |
| Renderer | http://localhost:8081 |
| API health | http://localhost:8080/health |
| Renderer readiness | http://localhost:8081/health/ready |
| OpenAPI | http://localhost:8080/openapi/v1.json |
| Aspire dashboard (traces) | http://localhost:18888 |
| MinIO console | http://localhost:9001 |
| Postgres | localhost:5433 |

Postgres is published on **5433**, not 5432. A locally installed PostgreSQL binds the
default port and both listeners then answer on localhost, with an authentication failure as
the only clue. Override with `POSTGRES_PORT` in `.env`.

Migrations run as their own `migrate` service, which the API and Worker wait on. They are
never applied automatically at startup — several replicas racing through a schema change
is how you corrupt one.

## Try the walking skeleton

```bash
# 1. Create a tenant (this endpoint runs outside tenant scope, by design)
curl -s -X POST http://localhost:8080/api/tenants \
  -H 'Content-Type: application/json' \
  -d '{"name":"Appointso","slug":"appointso"}'

# 2. Everything else needs a tenant. Use the id returned above.
TENANT=<id-from-step-1>

curl -s -X POST http://localhost:8080/api/brands \
  -H "X-Tenant-Id: $TENANT" -H 'Content-Type: application/json' \
  -d '{"name":"Appointso","website":"https://appointso.com","timeZoneId":"Europe/Zagreb","languages":["hr","en"]}'

# 3. Enqueue a job. A worker replica leases and executes it; check the trace in Aspire.
curl -s -X POST http://localhost:8080/api/diagnostics/ping \
  -H "X-Tenant-Id: $TENANT" -H 'Content-Type: application/json' \
  -d '{"message":"hello","idempotencyKey":"demo-1"}'

# Repeating step 3 with the same idempotencyKey returns the same jobId and queues nothing.

# 4. Watch it reach a terminal state
curl -s "http://localhost:8080/api/diagnostics/jobs?state=Succeeded" -H "X-Tenant-Id: $TENANT"

# 5. Round-trip an object and get a 15-minute presigned URL.
#    The signed link returns 200; strip the query string and MinIO returns 403.
curl -s -X POST http://localhost:8080/api/diagnostics/storage -H "X-Tenant-Id: $TENANT"
```

`ObjectStorage:ServiceUrl` is what the server talks to; `ObjectStorage:PublicServiceUrl`
is what a browser can reach. They differ inside Docker, and because the host is part of
the signature a presigned URL has to be signed against the public endpoint from the
start — it cannot be rewritten afterwards.

Tenancy is currently resolved from the `X-Tenant-Id` header, because the MVP has one
operator and no public signup. When authentication lands, the claim replaces the header
inside `TenantResolutionMiddleware` and nothing downstream changes.

## The Brand Brain

```bash
# Seed the Appointso brand: profile, 12 product facts, 2 personas, weekly quota.
docker compose run --rm api --seed

# What the agents will actually be given, and whether it fits the prompt budget.
curl -s "http://localhost:8080/api/brands/$BRAND/brand-block" -H "X-Tenant-Id: $TENANT"

# What is missing before content would be any good.
curl -s "http://localhost:8080/api/brands/$BRAND/snapshot" -H "X-Tenant-Id: $TENANT"

# Upload an asset. Validated by signature, re-encoded, stripped of metadata.
curl -s -X POST "http://localhost:8080/api/brands/$BRAND/assets" -H "X-Tenant-Id: $TENANT"   -F kind=ProductScreenshot -F "description=Booking screen" -F tags=ui,booking   -F "file=@booking.png"
```

The split between relational and JSON is deliberate:

| Lives in | What | Why |
|---|---|---|
| Columns | Product facts, personas, quotas, asset metadata | Cited by key, counted against, filtered on |
| `jsonb` | Visual identity, voice, messaging | Read wholesale, never joined, shape still moving |
| Object storage | The bytes | Content-addressed; the same file twice is one object |
| `brand_profile_versions` | A hashed, immutable snapshot | The only way to answer "what did the agents see?" six weeks later |

**Product facts are the load-bearing part.** Every factual claim a copywriter makes must
cite a fact key, and the marketing QA agent verifies the cited fact supports the claim. A
claim citing nothing is rejected without asking a model anything.

`POST /api/brands/{id}/versions` freezes the brain and returns its hash. An unchanged brand
reuses its version instead of accumulating identical rows.

### What upload refuses

Magic bytes decide the format, never the extension. Every raster image is re-encoded from
decoded pixels, which strips EXIF, GPS, ICC profiles and any appended payload in one step.
Dimensions are read from the header before decoding, so a decompression bomb is refused
while it is still small. SVG is sanitised through an allow-list, rasterised, and the vector
original is discarded — it would otherwise be XML loaded into the renderer's browser.

## Language models

Nothing can spend a cent until you add a key and flip one switch.

```bash
cp .env.example .env        # then fill in the key for the provider you want
```

```ini
Ai__Providers__Anthropic__ApiKey=sk-ant-...     # console.anthropic.com/settings/keys
Ai__Providers__OpenAi__ApiKey=sk-...            # platform.openai.com/api-keys
Ai__Enabled=true
```

You only need the key for the provider your profiles actually name.

### Choosing a provider

**Per profile, not per deployment.** Each agent declares its own vendor in
`appsettings.json`, so the strategist can run on one and the copywriter on another:

```json
"copywriter": {
  "Provider": "OpenAi",
  "ModelId": "gpt-5",
  "InputPricePerMillion": 125000000,
  "OutputPricePerMillion": 1000000000
}
```

Nothing else changes — same prompts, same validators, same budget, same cassettes. That is
what makes a side-by-side comparison of two models honest: identical brief, identical
checks, identical week.

Each vendor has its own adapter on its own official SDK. Neither is routed through the
other's compatibility shim, which would quietly forfeit schema-enforced output and
misreport which model actually ran.

### What the layer guarantees

| Concern | How |
|---|---|
| Output shape | A JSON Schema goes in and is enforced by the provider. There is no "give me some text" method |
| Spend | Refused **before** the call, not reported after. A budget enforced afterwards is a report |
| Cost accuracy | Integer micro-cents, priced from rates snapshotted onto every ledger row |
| Retries | Transport failures only. A schema rejection means the prompt is wrong, and retrying costs money to fail the same way |
| Tests without a key | Cassettes replay recorded responses. A missing cassette is an error, never a silent live call |

Prompts are embedded files with front matter, not string literals, so a change is a
reviewable diff and any past output traces back to exact text by hash. Rendering is strict
in both directions: a missing variable would otherwise send the model the literal
`{{brand_block}}`, which no model complains about and every reader misreads as working.

## The renderer

```bash
curl -s http://localhost:8081/templates
```

| Template | Ratios | Needs a screenshot | Notes |
|---|---|---|---|
| `hook-overlay` | 4:5, 1:1, 9:16 | no | One claim over a generated or brand background, with a scrim that guarantees contrast |
| `phone-floating` | 4:5, 9:16 | yes | Copy above, real product UI in a device frame below |
| `feature-highlight` | 4:5, 1:1 | yes | Browser-chrome card holding the real product UI |
| `testimonial` | 4:5, 1:1 | no | Quote-led social proof; stays available when the asset library is thin |
| `safe-mode` | 4:5, 1:1, 9:16 | no | The escalation ladder's last rung: generous budgets, no imagery, renders cleanly for almost any input |

`POST /render/image` takes a template id, an aspect ratio, brand tokens, slot copy and
base64 assets. It returns the image, a mask render per immutable slot, and a **render
report**: every slot's measured box, whether it overflows, its contrast against what is
actually behind it, and how much of it is covered. That report is what makes deterministic
QA cheap in Phase 4 — the layout engine already knows every answer, so nothing has to be
inferred from pixels.

`POST /compare` verifies a product screenshot survived the render. See §10 of the plan; the
calibrated thresholds and the measurements behind them are documented on
`FidelityThresholds`.

### Rules the renderer enforces, not merely hopes for

- **The network is blocked.** Everything is inlined; a request leaving the page is recorded
  in the report and fails the render rather than silently falling back to a system font.
- **`object-fit: contain` is forced on immutable slots.** `cover` crops, and a cropped
  screenshot is an altered screenshot.
- **Copy over budget is refused, not clipped.** The manifest budget is the contract the
  copywriter was briefed against.
- **No animation, no randomness, no clock.** The same request renders the same bytes, and a
  test asserts it.

## Tests

```bash
dotnet test                                     # everything
dotnet test tests/ContentPilot.UnitTests        # fast, no Docker
dotnet test tests/ContentPilot.RendererTests    # needs Chromium, not Docker
```

| Suite | Needs | What it protects |
|---|---|---|
| `UnitTests` | — | Job lifecycle, storage keys, tenant scope, backoff, domain invariants |
| `ArchitectureTests` | — | Layering, agent isolation, every tenant-owned entity is filtered |
| `IntegrationTests` | Docker | Real Postgres and MinIO: isolation, leasing, idempotency, presigned URLs |
| `WorkflowTests` | Docker | Two competing worker hosts execute one job exactly once |
| `RendererTests` | Chromium | Templates render, budgets hold, screenshots survive intact |

`ApiEndpointTests` boots the real host over HTTP. A whole class of defect lives only at
that boundary — parameter binding, status codes, middleware order — and is invisible to the
service-level tests.

Docker-backed tests **skip** rather than fail when no daemon is reachable, so a machine
without Docker still gets a green unit, architecture and renderer suite.

Renderer tests write every frame they produce to `artifacts/render/`. Look at them; the
assertions can prove the pipeline works, never that the design is good.

> **Docker Engine older than 25?** Testcontainers negotiates API 1.44 and an older daemon
> refuses it. Either upgrade Docker Desktop, or run:
> `DOCKER_API_VERSION=1.43 dotnet test`

## Migrations

```bash
dotnet ef migrations add <Name> \
  --project src/ContentPilot.Infrastructure \
  --startup-project src/ContentPilot.Api \
  --output-dir Persistence/Migrations
```

## Layout

```
src/
  ContentPilot.Domain               entities and invariants; depends on nothing
    Branding/                       brand, profile, facts, personas, assets, preferences
  ContentPilot.Application          abstractions, use cases; agents and orchestration land here
  ContentPilot.Infrastructure       EF Core, Postgres queue, S3 storage, telemetry
  ContentPilot.Rendering.Contracts  DTOs shared with the renderer, and nothing else
  ContentPilot.Renderer             standalone service: templates, Chromium, image metrics
    Fonts/                          embedded woff2, latin + latin-ext, no network fetch
    Templates/Static/               one .razor component and one .manifest.json each
  ContentPilot.Api                  thin HTTP host; enqueues work, never consumes it
  ContentPilot.Worker               thin host; consumes work
tests/
  Shared/                           linked test helpers
docker/                             one image for api and worker, one for the renderer
```

## Conventions worth knowing before you contribute

- **The orchestrator decides what happens next — nothing else.** Agents return values.
  They cannot enqueue jobs, write state, or call each other, and an architecture test
  fails the build if that ever changes.
- **The template manifest is the single source of truth.** Slot budgets, aspect ratios,
  occlusion limits and safe areas live in JSON, read by the template selector, the copy
  brief and QA alike. A template without a manifest fails at startup.
- **Every tenant-owned entity is filtered automatically** by a reflection loop in
  `AppDbContext`. Implement `ITenantOwned` and isolation comes for free.
- **Enqueue does not save.** `IJobQueue.EnqueueAsync` enlists in the caller's unit of work,
  so a job and the state change it advances commit together or not at all.
- **Storage keys are tenant-prefixed by construction.** `ObjectKey.ForTenant` is the only
  way to make one.
