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
src/ContentPilot.Api/Endpoints/AdminEndpoints.cs
tests/ContentPilot.IntegrationTests/ApiEndpointTests.cs
tests/ContentPilot.IntegrationTests/AdminEndpointTests.cs

## notes

Implement the remaining `TODO-NEXT.md` frontend work: Brand Brain personas, product facts
and content preferences, followed by rendered-image preview and campaign-list cost/QA
summaries. The review found that item detail exposes no rendered asset URL, so the claim also
covers one campaign image-URL endpoint and its integration coverage.
Phase 9 follow-up adds read-only eval trends to the existing admin endpoint and exposes the
existing dead-job/stuck-run/eval data in the minimal Angular operator UI.
