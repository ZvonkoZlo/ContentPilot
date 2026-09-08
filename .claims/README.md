# Claims

One file per agent. **That is the whole design** — if ownership lived in a single shared
file, the file recording who may edit what would itself be the thing two agents conflict on.

## Claiming

1. Copy `EXAMPLE.md` to `<your-name>.md`, matching the name you passed to
   `scripts/setup-agent.sh`.
2. List the paths you own, one per line under `## paths`. Directories end with `/`.
3. Commit and push **before you start writing code**, so the other agent's hook can see it.
4. When you merge, set `status: done`. The paths are released.

## How it is enforced

`.githooks/pre-commit` reads every claim file that is not yours and refuses a commit that
touches a path claimed by an active agent. It also blocks a new EF migration unless your
claim carries `migrations: true`.

The hook only knows what is committed. A claim sitting unstaged on your machine protects
nothing.

## Rules

- Claim before you code, not after. A claim staked once you are half done is a conflict
  report, not a claim.
- Claim only what you will actually write. Over-claiming blocks the other agent for nothing.
- Exactly one agent may hold `migrations: true` at a time.
- Paths listed in `shared:` inside `PARALLEL-WORK.md` cannot be claimed by anyone — they are
  append-only for everybody.
