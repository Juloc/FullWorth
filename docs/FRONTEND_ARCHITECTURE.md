# FullWorth frontend architecture

Status: permanent architecture contract  
Applies to: `src/FullWorth.Web/wwwroot`

## Purpose

FullWorth remains a server-hosted, local-first PWA with a vanilla ES-module frontend. The goal is not a framework rewrite. The goal is clear ownership: routing, shared services and UI primitives live in shared modules; each finance feature owns its rendering and behavior; backend services remain the source of truth for financial calculations.

## Current composition

```text
app.js
  -> core/state.js
  -> core/services.js / core/api.js
  -> core/router.js
  -> core/navigation.js
  -> core/event-bus.js
  -> core/feature-registry.js
  -> ui/*
  -> features/*
```

`app.js` is the composition/bootstrap layer. Feature logic must not move back into it.

## Routing and navigation

- `core/router.js` owns URL writes.
- `core/navigation.js` is the module-level navigation entry point used by features.
- Browser history must not be written directly by feature modules.
- Do not navigate by triggering another button's `.click()`.
- Do not add `window.fw*` or other feature-navigation globals.
- Account/group/category/merchant scope belongs in URL-backed route state so reload/back/forward preserve it.

## Feature lifecycle

`core/feature-registry.js` owns active-feature transitions and supports a cleanup callback.

Feature owners should converge on:

```js
export async function mount(ctx, route) {
  // render and bind
  return () => {
    // remove listeners / cancel work
  };
}
```

Existing `renderX(ctx)` modules may be migrated incrementally. New routed features should use explicit lifecycle cleanup from the start.

Rules:

- no global MutationObserver used to repair another feature's DOM
- no retry/poll loops waiting for another renderer
- no cross-feature DOM decoration after render
- no unbounded global listeners without an owner/cleanup path

## API boundary

- Shared BFF/API behavior lives in `core/api.js` and `core/services.js`.
- Feature modules call the shared client/context; they do not construct `/bff/backend` or `/bff/banking` URLs directly.
- Backend owns authorization, finance calculations, normalization, transfer/refund semantics, category descendants, recurring-contract cadence and analytics truth.
- Frontend code may format requests and presentation state; it must not duplicate finance truth.

## Shared UI

Shared primitives under `ui/` are preferred over feature-local base implementations.

Required shared concepts include:

- dialog / sheet behavior
- confirm
- button roles
- toast
- money formatting and semantic money variants
- identity/logo/category fallback
- category picker
- chart interaction
- loading / empty / error states as they are consolidated

Only `ui/dialog.js` may create native `<dialog>` elements.

## Money semantics

`ui/money.js` owns both formatting and semantic presentation variants:

```text
neutral
income
warning
danger
debt
muted
```

Ordinary debit/spending/contract cost is neutral. Red is reserved for an actual problem/risk state or an intentionally distinct debt/loss presentation. A negative numeric sign is not, by itself, a danger state.

## Period semantics

The finance UI separates:

```text
active bucket
preview/history window
comparison bucket
completed-period trailing average
scope
```

A selected month is one month. A 12-month chart preview does not turn the headline into a 12-month sum. Running current periods are excluded from completed trailing averages.

Dashboard previews and Analytics must use the same period semantics.

## Identity and drill-down

Stable identifiers are preferred over display text:

- accountId
- accountGroupId
- categoryId
- merchantId

Text matching is a search fallback, not the canonical aggregation identity.

Category overview uses a disjoint hierarchy level. Parent categories include descendants once; drill-down may then expose children and finally matching transactions.

## Accounts

Accounts structural migration is approved. The visible account/group model stays stable unless a separate UX change is requested.

Current direction:

- `features/accounts.js` owns account/banking workflows and rendering.
- `features/accounts-presentation.js` is owned presentation/editing submodule and must continue shrinking.
- Accounts UX must not decorate Dashboard/Wealth DOM.
- Future account presentation logic should move into the Accounts owner or a shared account-identity primitive, not another observer/patch layer.

## CSS

Do not solve structural problems by stacking more global override files.

Incremental target:

```text
styles/
  tokens.css
  shell.css
  components.css
  responsive.css
  features/
```

Until the split is complete, changes in `app.css` must still follow semantic tokens and component ownership. Theme files may change presentation, not repair feature behavior.

## Architecture guards

`tests/FullWorth.Web.Tests/FrontendArchitectureGuardTests.cs` is part of this contract. Temporary legacy allow-lists may only shrink.

The guards cover, among other things:

- native dialog factories
- direct BFF access
- global fetch monkey patches
- native confirms
- global DOM patch observers
- new installer/final/parity patch-layer file names

## Definition of done

The frontend restructuring is complete when:

- routing and history are centralized
- routed features have explicit lifecycle cleanup
- app.js is composition/bootstrap only
- features own their own DOM
- shared API/dialog/button/money/identity primitives are used consistently
- no feature integration depends on observers, synthetic clicks, window globals or timing hacks
- finance calculations remain backend-owned
- service-worker shell entries match static module dependencies
- architecture tests prevent regression
