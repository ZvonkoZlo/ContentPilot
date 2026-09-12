# ContentPilot review UI (minimal, for testing)

A small Angular app for driving the ContentPilot API by hand, without curl/Postman. It is
**not** the Phase 8 review UI from the implementation plan (no auth, no design, no polish)
— it exists so a real campaign can be triggered and inspected end to end while the backend
is being tried out.

## Run it

```bash
npm install   # first time only
npm start     # ng serve, http://localhost:4200
```

The API must be running and reachable (`docker compose up`, or `dotnet run` per service)
with CORS allowing `http://localhost:4200` — already configured in `Program.cs` for local
development.

## Using it

1. **API URL** — defaults to `http://localhost:8080` (the compose file's API port). Change it
   if the API runs elsewhere, then click away from the field to apply.
2. **Tenant** — pick one from the dropdown (populated from `GET /api/tenants`), or create one
   first via `POST /api/tenants` (no UI for that here — it's a one-time setup step). The
   golden tenant seeded by `--seed` shows up here.
3. **Brand** — picked from the dropdown once a tenant is selected.
4. From there: **Generate now** triggers a campaign, the table lists campaigns for the brand,
   and opening one shows its items, cost, QA pass rate, and a **Build / get ZIP download
   link** button. Opening an item shows its findings, run tree, and agent runs, with
   **Approve** / **Reject** for anything stuck in `NeedsHumanReview` and a 1–5 rating field.

Settings persist in the browser's `localStorage`, so a reload keeps the same tenant/brand.

## What this deliberately does not do

No routing guards, no loading skeletons, no optimistic updates, no design system — every
action just calls the matching endpoint and re-fetches. That trade is intentional: the goal
is to see the backend work, not to build the product's real UI on a smaller budget than it
needs.
