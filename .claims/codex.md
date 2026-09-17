---
agent: codex
phase: brand-review-ui
branch: todo-brand-review-ui
status: active
migrations: false
updated: 2026-09-17
---

## paths

frontend/
src/ContentPilot.Api/Endpoints/CampaignEndpoints.cs
tests/ContentPilot.IntegrationTests/ApiEndpointTests.cs

## notes

Implement the remaining `TODO-NEXT.md` frontend work: Brand Brain personas, product facts
and content preferences, followed by rendered-image preview and campaign-list cost/QA
summaries. The review found that item detail exposes no rendered asset URL, so the claim also
covers one campaign image-URL endpoint and its integration coverage.
