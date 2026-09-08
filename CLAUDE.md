# CLAUDE.md — read this before you touch anything

This repository is worked on by more than one AI agent at the same time. This file is the
entry point for **Claude Code**; agents that read `AGENTS.md` (Codex among them) get
[AGENTS.md](AGENTS.md), which says the same thing. Both point at one contract:
**[PARALLEL-WORK.md](PARALLEL-WORK.md)** — the ownership map, the collision points, and the
rules. Read it before your first edit.

## Run this once, before anything else

```bash
./scripts/setup-agent.sh <your-name>      # e.g. ./scripts/setup-agent.sh claude
```

It records who you are and installs the pre-commit hook that enforces the rules below. A
commit without it is a commit nobody checked.

## Claim a phase before you write code

Ownership lives in `.claims/<your-name>.md` — **one file per agent, so claiming can never
conflict.** Copy `.claims/EXAMPLE.md`, list the paths you own, commit it, and push before
you start. Release it by setting `status: done` when you merge.

The hook reads every other agent's claim and refuses a commit that touches their paths.

## The five rules that matter

1. **Do not run `dotnet ef migrations add`** unless `.claims/` says you own migrations.
   Two migrations generated in parallel branch from the same snapshot and produce a chain
   EF cannot linearise. Write the entity and its configuration, then stop and note it in
   your handover section of PARALLEL-WORK.md.

2. **Three files are append-only for everyone:**
   `src/ContentPilot.Infrastructure/Persistence/AppDbContext.cs`,
   `src/ContentPilot.Infrastructure/DependencyInjection.cs`,
   `src/ContentPilot.Api/Program.cs`.
   Add your line at the end of the relevant block. Never reorder, never reformat — a
   whitespace change turns a one-line conflict into a whole-file one. Put the real
   registration in your own file and call it with one line.

3. **`src/ContentPilot.Rendering.Contracts` is append-only.** New types in new files. Never
   modify an existing record, enum or property — Phase 2's renderer, its 20 tests and the
   calibrated fidelity thresholds all depend on the current shapes.

4. **Stay in your lane.** Everything outside your claimed paths is someone else's, including
   things that look obviously broken. Write it in your handover notes instead of fixing it.

5. **`main` always builds and stays green.** Before merging: `dotnet build` clean (warnings
   are errors here), your suites green, **and every suite you did not write still green**.

## Environment, so two agents do not fight over containers

```bash
export COMPOSE_PROJECT_NAME=contentpilot-<your-name>
```

Own ports in a git-ignored `.env` (`POSTGRES_PORT`, `MINIO_PORT`, `MINIO_CONSOLE_PORT`) —
the host already runs PostgreSQL 16 on 5432, which is why the default is 5433.

**Never run `docker compose down -v`.** It destroys volumes the other agent is using.

## What this project is

An autonomous content platform: a business's Brand Brain in, a week of publishable social
content out. A deterministic pipeline with a few narrow LLM agents in it — not an agent
system with a rendering step. The full technical plan is
[IMPLEMENTATION-PLAN.md](IMPLEMENTATION-PLAN.md); how to run what exists is
[README.md](README.md).

Conventions worth knowing before you write a line:

- **The orchestrator decides what happens next — nothing else.** Agents return values. They
  cannot enqueue jobs, write state, or call each other, and an architecture test fails the
  build if that changes.
- **Template manifests are the single source of truth** for slot budgets, aspect ratios and
  occlusion limits.
- **Every tenant-owned entity is filtered automatically.** Implement `ITenantOwned`.
- **Enqueue does not save.** `IJobQueue.EnqueueAsync` enlists in the caller's transaction.
