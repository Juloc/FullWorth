# Frontend Architecture Cleanup — Handoff

Branch: `main`  
Status: active incremental cleanup; do not restart or create a parallel frontend stack.

Permanent contract:

- `docs/FRONTEND_ARCHITECTURE.md`
- `docs/FRONTEND_ARCHITECTURE_CLEANUP_PLAN.md`
- `docs/FRONTEND_RESTRUCTURE_HANDOFF.md`

## Current architecture

The shipped frontend already has:

- shared API/services/state/router/i18n core
- module navigation in `core/navigation.js`
- explicit app events in `core/event-bus.js`
- feature activation/unmount support in `core/feature-registry.js`
- shared dialog/confirm/button/toast/money primitives
- semantic money variants: neutral / income / warning / danger / debt / muted
- centralized route writes; feature navigation no longer depends on `window.fw*`
- Accounts + banking owner extracted to `features/accounts.js`
- Settings/security owner extracted to `features/settings.js`
- `app.js` reduced to shell/bootstrap/composition responsibilities
- Accounts observer/polling/synthetic-click integration removed
- Accounts UX no longer decorates Dashboard or Wealth DOM
- Purchase advanced helpers use shared BFF URL + confirm/dialog infrastructure
- PWA shell includes extracted Accounts and Settings owners

Accounts structural migration is approved. Preserve the established visible Accounts UX unless a separate UX change is requested.

## Finance semantics already fixed

Do not regress these:

- selected period is distinct from chart history
- completed trailing averages exclude the active/running bucket
- arbitrary-range category analytics now returns real Average3/6/12 values
- category overview is root-only and drills root -> children -> transactions
- merchant analytics uses canonical merchantId where available
- transaction merchant filtering accepts merchantId and resolves aliases server-side
- mixed-cycle budgets are not blindly summed
- normal spending/debit/contract amounts are neutral, not danger-red
- Dashboard and Analytics use the same active-period semantics
- net worth remains point-in-time and owns its own account drill-down

## Architecture rules

Do not introduce:

- direct `/bff/backend` or `/bff/banking` URLs in feature modules
- feature-local native dialog factories
- native `confirm()` for normal app flows
- global MutationObserver repair/decorator layers
- synthetic `.click()` navigation
- polling to discover another feature's freshly-created DOM/entity
- `window.fw*` integration APIs
- direct feature-owned history writes
- new installer/final/parity/completion patch layers

The architecture guard allow-lists may only shrink.

## Remaining structural work

1. Continue shrinking remaining architecture allow-lists, especially legacy native-confirm and observer exceptions.
2. Continue moving shell concerns out of `app.js` only when there is a clear owner; do not turn this into another big-bang rewrite.
3. Shrink `features/accounts-presentation.js` by moving same-domain presentation logic into the Accounts owner/shared account identity primitives. Do not reintroduce cross-feature decoration.
4. Consolidate Purchase/Wealth layered helper modules into explicit feature submodules where useful.
5. Split monolithic CSS into `styles/tokens.css`, shell/components/responsive and feature-owned styles after behavioral architecture is stable.
6. Retire the historical `parity-completion.css` name by folding its live rules into the final styles split rather than deleting live behavior.

## Validation

After each block:

- syntax-check changed JS modules
- keep `sw.js` shell dependencies current
- shrink architecture guard allow-lists when violations are removed
- preserve URL-backed scope/deep links
- add numerical backend tests for finance semantics
- add web architecture/behavior guards for ownership boundaries

CI remains manual by repository policy (`workflow_dispatch`). Do not add push/PR triggers merely to obtain a run.
