# FullWorth Frontend Architecture Cleanup Plan

Current continuation handoff: `docs/FRONTEND_ARCHITECTURE_HANDOFF.md`

Status: approved architecture cleanup plan — in progress (core/UI primitives, guards, Accounts/Settings extraction and major workaround removal are shipped; Phase 8 CSS split and further feature consolidation remain)  
Scope: FullWorth.Web frontend  
Constraint: Accounts visible UX remains stable; structural migration was explicitly approved on 2026-09-07.

## Goal

Turn the current frontend into a stable, modular vanilla-ES-module architecture so that future reworks, features, fixes and removals do not require additional DOM-patch, installer, parity, "final UI" or workaround layers.

This is not a framework rewrite. FullWorth stays a server-hosted web app/PWA with static browser assets.

## Non-negotiable rules

1. A feature owns its own DOM and must not patch another feature after render.
2. Only the shared API client may call `/bff/backend` or `/bff/banking`.
3. Only the shared dialog module may create `<dialog>` elements.
4. Normal action buttons use exactly three visual roles: Primary, Secondary and Danger.
5. No MutationObserver may be used to repair or decorate another feature's DOM.
6. No polling with repeated `setTimeout` to wait for another renderer or newly-created entity.
7. No synthetic clicking of another feature's buttons as an integration mechanism.
8. No new `*-installer.js`, `*-final-ui.js`, `*-parity-ui.js` or equivalent patch layers.
9. Feature calculations and financial business rules stay in the backend.
10. New user-visible strings use the shared locale files.
11. Shared design tokens define spacing, radii, colors, shadows and breakpoints.
12. Architecture guards in tests/CI must enforce these rules.

## Target structure

```text
wwwroot/
  app.js

  core/
    api.js
    router.js
    state.js
    i18n.js
    feature-registry.js

  ui/
    dialog.js
    buttons.js
    toast.js
    shell.js
    money.js
    privacy.js
    identity.js
    category-picker.js
    chart-scrubber.js

  styles/
    tokens.css
    reset.css
    shell.css
    buttons.css
    forms.css
    rows.css
    cards.css
    dialogs.css
    responsive.css

  features/
    transactions/
    contracts/
    networth/
    analytics/
    purchases/
    categories/
    rules/
    settings/
    ...
```

Migration is incremental. No big-bang rewrite is required.

Current status: `core/` (shipped as `api.js`, `router.js`, `state.js`, `i18n.js`, `feature-registry.js` plus an added `services.js`) and the `ui/` primitives are shipped as flat modules. The `styles/` split (Phase 8) and the per-feature `features/<name>/` directories (Phase 5) are still the target — features currently live as flat `features/*.js` files and shared CSS still lives in the monolithic top-level `*.css` files.

## Phase 1 — Architecture contract and guards

Guards: SHIPPED and continuously tightened. Architecture tests live at `tests/FullWorth.Web.Tests/FrontendArchitectureGuardTests.cs` and cover:
  - native dialog creation outside the shared dialog module
  - direct `/bff/backend` or `/bff/banking` URLs in feature code
  - global fetch monkey patches
  - native `confirm` / `window.confirm`
  - global MutationObserver repair/decorator layers
  - installer/final/parity/completion patch module names
  - bootstrap ownership
  - Settings ownership
  - Accounts shared-core integration and removal of old navigation globals

The direct-BFF, native-confirm and patch-filename guards no longer need legacy feature allow-lists. MutationObserver exceptions are limited to reviewed shared UI infrastructure. The remaining fetch monkey-patch exception is `security/browser-fetch.js`.

Still useful to add later:
  - a focused guard for feature-local button-role drift
  - a targeted guard for timing/polling integration hacks without banning legitimate external-status polling

Permanent contract: SHIPPED at `docs/FRONTEND_ARCHITECTURE.md`.

Existing violations may be temporarily allow-listed and removed phase by phase. The allow-list must only shrink.

## Phase 2 — Shared core

### `core/api.js`
Own:
- backend and banking BFF routing
- FullWorth Space scoping
- JSON/FormData handling
- standardized errors
- request deduplication
- cache invalidation after mutations
- AbortController support

Remove over time:
- global `window.fetch` monkey patch
- local `api()`, `req()`, `withSpace()` helpers
- duplicated response/error parsing

### `core/router.js`
Own:
- route registration
- deep links
- history/back/forward
- feature mount/unmount
- route query state

### `core/state.js`
Own global state only:
- current user/session presentation state
- active FullWorth Space
- locale
- theme
- privacy preference

Feature-local state stays inside the feature.

### `core/i18n.js`
Own:
- locale loading
- translation lookup
- document language
- shared formatting hooks

No feature-local DE/EN dictionaries for normal app UI.

## Phase 3 — Shared UI primitives

### Dialogs
Only `ui/dialog.js` creates native dialogs.

Supported variants:
- dialog
- sheet
- fullscreen
- drawer
- confirm

Shared behavior:
- header and title
- close control
- Esc handling
- focus return
- cleanup/removal
- mobile fullscreen rules
- bottom-sheet behavior
- swipe-to-close where appropriate
- action footer
- accessible names

Remove every feature-local dialog factory after migration.

### Buttons
Shared roles:
- Primary
- Secondary
- Danger

Separate primitives:
- IconButton
- Chip
- Toggle
- navigation controls

No feature-specific action-button styling.

### Other shared primitives
Consolidate:
- Toast
- EmptyState
- ErrorState
- LoadingSkeleton
- SectionCard
- MoneyValue
- Identity / Brand / Category fallback
- FilterSheet
- PeriodPicker
- ScopePicker
- StatusBadge

## Phase 4 — Reduce `app.js`

Target: `app.js` becomes bootstrapping and composition only.

Already moved out:
- banking workflows and dialogs -> `features/accounts.js`
- account management logic -> `features/accounts.js`
- budget workflows -> `features/budgets.js`
- Settings/security workflows -> `features/settings.js`
- global search -> `ui/global-search.js`
- feature-specific transaction/analytics/contracts/net-worth/etc. rendering -> feature modules

Still in `app.js`:
- boot/session/capability composition
- theme/locale/space shell state
- sidebar/top-level shell behavior
- router/view activation
- shared context assembly and feature registration

Target responsibility:
- initialize core services
- initialize shell
- register features
- boot router

## Phase 5 — Feature ownership

Each feature receives an explicit owner module, for example:

```text
features/contracts/
  index.js
  view.js
  dialogs.js
  state.js
  contracts.css
```

Recommended lifecycle:

```js
export const feature = {
  route: '/contracts',
  mount(ctx) {},
  render(ctx) {},
  refresh(ctx) {},
  unmount() {}
};
```

A feature may use shared core/UI modules but must not mutate another feature's DOM.

Page header metadata and contextual actions should be declared by the active feature instead of maintained in a central feature-specific switch/table.

## Phase 6 — Remove workaround layers

Prioritize modules that currently behave as post-render patches or parallel implementations.

Remove patterns such as:
- MutationObserver-driven decoration
- clone/replace of another renderer's controls
- DOM scraping to rediscover entity identity
- synthetic button clicks
- resource-timing lookup to infer IDs
- delayed polling to find freshly-created objects
- duplicate local API clients
- duplicate modal factories

Integrate functionality into the actual owning feature instead.

## Phase 7 — Purchases / parity / investments cleanup

Consolidate the current layered modules such as:
- `*-installer.js`
- `*-advanced-actions.js`
- `*-parity-ui.js`
- `*-completion-ui.js`
- `*-final-ui.js`
- `*-extra.js`

into explicit feature submodules with one owner and shared infrastructure.

Do not remove functionality during consolidation.

## Phase 8 — CSS architecture

Stop growing the monolithic `app.css`.

Shared structural CSS moves to `styles/`.
Feature-only rules move beside their owning feature.

Rules:
- semantic tokens only
- no feature-local random radii/shadows/breakpoints
- `design-depth.css` may only add visual depth/theme presentation, not repair structural layout
- visual themes override tokens/component presentation, not feature behavior

## Phase 9 — Business logic boundary

Frontend may:
- render
- collect input
- perform basic form validation
- apply purely visual sorting/filtering when appropriate

Backend owns:
- money calculations
- contract cadence/lifecycle calculations
- analytics totals
- matching/detection logic
- authorization/capabilities
- financial state transitions

## Phase 10 — Accounts structural migration — APPROVED / IN PROGRESS

Explicit approval was given on 2026-09-07 to finish the remaining frontend restructuring while preserving the established Accounts UX.

Completed structural steps:

- `features/accounts.js` owns account rendering, account/group actions and banking workflows.
- Accounts UX uses the shared API client instead of a feature-local BFF client.
- Scoped navigation uses the shared navigation service instead of synthetic button clicks or window globals.
- The global MutationObserver integration loop was removed.
- Newly-created manual-account identity is handed off by exact entity id instead of polling for a freshly-created row.
- Accounts UX no longer decorates Dashboard or Wealth DOM.
- Dashboard and Net Worth own their account drill-down rendering.
- Mobile account actions collapse into an owned overflow sheet instead of overflowing the row.
- Account/group transaction drill-down behavior is preserved.

Remaining Accounts work is consolidation: move the remaining same-domain visual/group-edit decoration from `accounts-presentation.js` into the Accounts owner/shared account identity primitives, then retire the presentation submodule without changing the established UX.

## Delivery order

1. Architecture document and CI guards
2. Shared API client
3. Shared dialog/button primitives
4. Router/state/i18n extraction
5. Reduce `app.js`
6. Migrate normal feature modules
7. Consolidate purchase/parity/investment patch layers
8. Split shared and feature CSS
9. Remove temporary architecture allow-list entries
10. Finish mechanical Accounts ownership extraction without changing visible UX

## Definition of done

The cleanup is complete when:

- `app.js` is a small bootstrap/composition module
- every feature has one clear owner
- no feature patches another feature's DOM
- all BFF access goes through the shared API client
- all native dialogs are created through the shared dialog module
- all normal action buttons use the shared variants
- no global fetch monkey patch is required
- no timing/polling workaround is required for feature integration
- no installer/final/parity patch layers remain
- architecture tests prevent these patterns from returning
- Accounts structural migration uses shared core services and contains no observer/synthetic-click integration
