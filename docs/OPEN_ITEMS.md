# Open items — to clarify & decide

A single index of the genuinely-unimplemented / decision-needed items surfaced by the 2026-09-07 doc↔code
reconciliation. The individual plan/spec docs keep the full detail; this is the short list to work through
together. Everything NOT listed here was verified as shipped.

## Structural (frontend architecture)
Source: `FRONTEND_RESTRUCTURE_HANDOFF.md`, `FRONTEND_ARCHITECTURE_CLEANUP_PLAN.md` (the doc's actual *Goal* — the
UX-semantics part is done, this structural part is not started).
- **Shrink `app.js`** (~1364 lines) to bootstrap/composition only — banking + account management are still inline.
- **Router ownership**: `core/router.js` is a thin helper; no single navigation API. `window.fwNavScope` is a
  global consumed across ~7 files. Introduce a real router + drop the global.
- **Page lifecycle**: no `mount(ctx, route) -> cleanup()` contract; features bind without teardown.
- **Feature dirs** (`features/<name>/{index,view,dialogs,state,css}`) and **`styles/` split**
  (tokens/shell/components/responsive) — not created (CSS is flat).
- Create `docs/FRONTEND_ARCHITECTURE.md` (the Phase-1 "permanent contract") — never written.

## Behaviour / finance-model gaps
- **Shared money-variant model** in `ui/money.js`: expose neutral/income/warning/danger/debt variants and use
  them centrally. Rows still choose the color class by sign (`amount < 0 ? 'negative' : 'positive'`). The red
  *alarm* is already neutralized in CSS, but the semantic model the spec asks for is missing.
- **Budgets sum mixed cycles** into one headline (`features/budgets.js` reduces weekly+monthly+pay-cycle items
  together) — evaluate each budget against its own period instead.
- **Backend arbitrary-range category averages** are hardcoded `0m` (`CategoryAnalyticsService.cs` Average3/6/12),
  so Week/Quarter/Year trailing averages are unsupported.
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
