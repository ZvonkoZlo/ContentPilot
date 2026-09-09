---
agent: claude
phase: 3
branch: main
status: active
migrations: true
updated: 2026-09-09
---

## paths

src/ContentPilot.Domain/Content/
src/ContentPilot.Domain/Observability/
src/ContentPilot.Application/Ai/
src/ContentPilot.Application/Agents/
src/ContentPilot.Application/Prompts/
src/ContentPilot.Application/ContentMemory/
src/ContentPilot.Infrastructure/Ai/
src/ContentPilot.Infrastructure/Persistence/Migrations/
tests/ContentPilot.UnitTests/Agents/
tests/ContentPilot.UnitTests/Ai/

## notes

Phase 3 — text agents and the LLM layer. **Paused at the user's request**; the domain
entities are committed and `Application/Ai/ILanguageModelClient.cs` is written but wired to
nothing.

Holds `migrations: true`. Nobody else runs `dotnet ef migrations add`. The content and
observability entities still have no migration — anyone adding an entity before that lands
should write the configuration, skip the migration, and say so in PARALLEL-WORK.md.
