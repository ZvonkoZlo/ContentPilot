---
agent: codex
phase: phase-8-weekly-email
branch: phase-8-weekly-email
status: done
migrations: false
updated: 2026-09-17
---

## paths

src/ContentPilot.Domain/Branding/Brand.cs
src/ContentPilot.Application/Abstractions/
src/ContentPilot.Infrastructure/Email/
src/ContentPilot.Infrastructure/Jobs/CampaignWorkflowJobHandler.cs
src/ContentPilot.Infrastructure/Persistence/Configurations/BrandConfiguration.cs
src/ContentPilot.Infrastructure/Persistence/Migrations/
src/ContentPilot.Infrastructure/ContentPilot.Infrastructure.csproj
src/ContentPilot.Api/Endpoints/BrandEndpoints.cs
src/ContentPilot.Api/appsettings.json
src/ContentPilot.Worker/appsettings.json
tests/ContentPilot.UnitTests/Email/
tests/ContentPilot.IntegrationTests/CampaignWorkflowJobHandlerTests.cs
tests/ContentPilot.IntegrationTests/ApiEndpointTests.cs
.env.example
docker-compose.yml
TODO-NEXT.md
AGENT-MAP.md
PARALLEL-WORK.md

## notes

Implement Phase 8 weekly campaign notification email with per-brand recipients, SMTP/STARTTLS
configuration, completion-time delivery, once-only EmailSentAt tracking, and automated coverage.
The claim owns migrations for the new Brand notification recipient field.

Merged to `main` through `51d5391` on 2026-09-17. Phase 8 weekly delivery, the per-brand
recipient endpoint/migration, MailKit transport configuration and completion/failure tests are
complete. Labelled VisualQA evals, carousel execution and observed-data calibration remain
external-input work as recorded in `TODO-NEXT.md`.
