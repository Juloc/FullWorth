# Open items — to clarify & decide

A single index of the genuinely-unimplemented / decision-needed items surfaced by the 2026-09-07 doc↔code
reconciliation. The individual plan/spec docs keep the full detail; this is the short list to work through
together. Everything NOT listed here was verified as shipped.

## Structural (frontend architecture)
Source: `FRONTEND_RESTRUCTURE_HANDOFF.md`, `FRONTEND_ARCHITECTURE_CLEANUP_PLAN.md`.

> **The owner landed a large refactor of this area concurrent with the 2026-09-07 audit**
> (commits `5a614be`…`9f9893f`: shared navigation + event core `core/navigation.js`/`core/event-bus.js`,
> per-feature **activate/unmount lifecycle** in `core/feature-registry.js`, centralized route writes, and a
> shared Dashboard↔Analytics period via `cycleWindow` `activeFrom`/`averageFrom`). Those are **done** and are
> removed from the list below; re-verify against current `main` before acting.

Still open:
- **Shrink `app.js`** — still ~1366 lines; it still owns `showView` and has banking + account management inline
  (~134 banking refs). Reduce it to bootstrap/composition.
- **Finish the `window.fwNavScope` → `core/navigation.js` migration** — the shared nav API exists
  (`installNavigation`/`navigate`) but 3 files still call the old global: `features/transactions.js`,
  `features/coach-shell.js`, `features/accounts-ux.js` (accounts is frozen).
- **Feature dirs** (`features/<name>/{index,view,dialogs,state,css}`) and the **`styles/` split**
  (tokens/shell/components/responsive) — not created (features are flat `*.js`, CSS is flat top-level).
- Create `docs/FRONTEND_ARCHITECTURE.md` (the Phase-1 "permanent contract") — never written.

## Behaviour / finance-model gaps
- **Shared money-variant model** in `ui/money.js`: expose neutral/income/warning/danger/debt variants and use
  them centrally. Rows still choose the color class by sign (`amount < 0 ? 'negative' : 'positive'`). The red
  *alarm* is already neutralized in CSS, but the semantic model the spec asks for is missing.
- ~~Budgets sum mixed cycles into one headline~~ — **fixed by the owner**: `features/budgets.js` now gates the
  headline on a comparable period window and shows "—" when budgets span different cycles.
- **Backend arbitrary-range category averages**: the service moved to
  `src/FullWorth.Backend/Modules/Analytics/Categories/CategoryAnalyticsService.cs`. Re-verify whether Average3/6/12
  are still `0m` for arbitrary ranges after the owner's "complete period and grouping semantics" refactor.
- **Canonical merchant identity**: analytics/drill key off counterparty *text* (`merchant=<name>`), not a
  `merchantId`, so an aggregate and its opened list can diverge.
- **Category overview list** `slice(0,6)` can still mix parent+child rows (the *total* is already root-only).
- **Analytics prev/next** now steps one bucket, but there is no shared PeriodState class unifying Dashboard +
  Analytics (low priority — they are conceptually aligned already).

## UI_UX_SPEC MVP items still mandated but absent
- Strict share/screenshot mode (§5), Alerts & actions widget (§8.6), portfolio-trend widget (§8.9), widget
  height presets + scope/visualization/forecast config (§6.2/§7), Dashboard & navigation settings incl.
  shared/separate layouts, bottom-nav slots, merchant-logo toggle (§6.3/§21). "Available until next income"
  (§8.3) currently echoes the account total — needs the real forecast calc.

## ⚠ Decisions needed (product/security)
- **Amazon import uses Playwright browser automation with stored Amazon credentials**, which conflicts with the
  `PRODUCT_DECISIONS.md` rule that the browser never receives third-party credentials. Decide: accept the
  Playwright path (and amend the security decision) **or** switch to manual/email-invoice/export adapters.
- **External-tool least-privilege permission model** and the stricter share/screenshot mode: implement or file
  as explicit future scope.

## Known visual bug (frozen area)
- **Accounts, mobile 375px**: account-row actions (Coach · chat · balance · folder · edit · ± · delete) overflow
  the viewport and the delete icon is clipped on every row. Fix = collapse row actions into a `⋯` overflow menu
  on mobile. Left untouched because Accounts (`features/accounts-ux.js`) is frozen pending owner approval.
