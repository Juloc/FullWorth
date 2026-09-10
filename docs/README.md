# Documentation

Reference documents describe how FullWorth behaves today. Anything still to be done lives in exactly
one place: [Improvement plan](IMPROVEMENT_PLAN.md).

## Architecture and code

- [Architecture](ARCHITECTURE.md) — unified container, module layout, data-access regimes, financial
  consistency invariants
- [Frontend architecture](FRONTEND_ARCHITECTURE.md) — feature lifecycle, CSS layers, the rules and the
  documented exceptions
- [Migrations](MIGRATIONS.md) — how the schema really changes
- [Import](IMPORT.md) — every importer, what it parses, and what it persists
- [Banking](BANKING.md) — Enable Banking and FinTS, sync cadence, health states, live validation
- [Cloud](CLOUD.md) — enrollment, observation outbox, knowledge packs, benchmarks, brand packs
- [AI Autopilot](AI_AUTOPILOT.md) — Coach, Intelligence, the Codex bridge and behaviour without AI
- [Compensation analyzer](COMPENSATION_ANALYZER.md) — the German payroll engine
- [Occupational pension](PENSION.md) — the bAV data model, what it reuses, and the money-direction rule
- [Categorization catalog](CATEGORIZATION_CATALOG.md) — the category tree and its keys

## Product

- [Roadmap](../ROADMAP.md) — scope and release priorities
- [Product decisions](PRODUCT_DECISIONS.md) — product rules and trade-offs
- [UI/UX specification](UI_UX_SPEC.md) — application behaviour and interface
- [Potential finance data ideas](POTENTIAL_FINANCE_DATA_IDEAS.md) — non-binding ideas that still need a
  decision before they become work

## Security, testing and operations

- [Security architecture](SECURITY_ARCHITECTURE.md) — trust boundaries, authentication, tenant
  isolation, headers, secrets
- [Banking safety](BANKING_SAFETY.md) — provider request limits and sync rules
- [Testing](TESTING.md) — the commands that work, what does not exist, and how to verify UI without
  credentials
- [Operations](OPERATIONS.md) — deployment, backups, restores, secret rotation
- [Release](RELEASE.md) — verification and image publishing

## Work in progress

- [Improvement plan](IMPROVEMENT_PLAN.md) — the single active plan, P0 to P3
- [Open items](OPEN_ITEMS.md) — smaller frontend and behaviour items
- [Mobile navigation plan](MOBILE_NAVIGATION_2_PLAN.md), [UX rework
  plan](SIMPLE_FINANCE_APP_UX_REWORK_PLAN.md), [UX gap-closure
  plan](SIMPLE_FINANCE_APP_UX_GAP_CLOSURE_PLAN.md), [AI Autopilot implementation
  plan](AI_AUTOPILOT_IMPLEMENTATION_PLAN.md) — older plans that still mix finished and open work; being
  triaged into the improvement plan

The session integration contract stays next to the code in
[src/FullWorth.Web/Modules/Sessions](../src/FullWorth.Web/Modules/Sessions/INTEGRATION.md).
