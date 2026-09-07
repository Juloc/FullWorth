# Frontend Architecture Cleanup — Handoff

Branch: `refactor/frontend-architecture-cleanup`

Continue the existing frontend architecture cleanup. Do **not** restart the work or create a new cleanup branch.

## First step

Before changing anything, merge the current `main` into `refactor/frontend-architecture-cleanup`. Keep doing this regularly while the cleanup runs.

## Important constraint

**Accounts are frozen for this cleanup until explicit approval.**

A separate agent is working on the desired Accounts UX. Do not restructure, visually redesign, remove or replace `features/accounts-ux.js` until the final Accounts direction is approved. Shared infrastructure may be prepared, but user-visible Accounts behavior must not be changed as part of this cleanup.

## Architecture goal

The permanent rules are documented in:

- `docs/FRONTEND_ARCHITECTURE_CLEANUP_PLAN.md`
- `docs/UI_UX_SPEC.md`

Core rule: a feature owns its own DOM. Do not add another post-render repair layer.

Do not introduce:

- feature-local native dialog factories
- direct `/bff/backend` or `/bff/banking` calls
- global fetch monkey patches
- MutationObserver-based feature repair
- polling with `setTimeout` to wait for another renderer
- synthetic clicks to integrate features
- Resource Timing / DOM scraping to rediscover entity IDs
- new `*-installer.js`, `*-final-ui.js`, `*-parity-ui.js` or equivalent patch layers
- native `confirm()` for normal app flows

Use shared infrastructure under `core/` and `ui/`.

## Work already completed on the cleanup branch

The branch already contains substantial cleanup work. Among other things:

- shared frontend core for API, state, router, feature registry and i18n
- shared dialog, confirm, button and toast primitives
- GET request deduplication moved into `core/api.js`
- global `window.fetch` monkey patch removed from `app.js`
- Budgets extracted from `app.js` into an owned feature module
- global search extracted from `app.js`
- category creation moved into the Categories owner
- portable export patch replaced by a direct owned action
- multiple local BFF clients migrated to shared API infrastructure
- dead category patch layers removed
- Investment/Wealth dialog and API workarounds migrated to shared infrastructure
- Wealth MutationObservers replaced with explicit Networth lifecycle refreshes
- Purchase workspace given explicit lifecycle and stable entity IDs
- Purchase Resource Timing lookup, synthetic clicks and timing-based reopen workarounds removed
- Purchase observers replaced by explicit refresh hooks
- Receipt Import migrated toward shared API/dialog lifecycle
- Receipt Scan Set uses the shared dialog primitive
- unused legacy Receipt Scan AI layer removed
- unreachable Tax patch layer removed
- unreachable parity/final/mobile-review/bulk/switcher patch layers and their CSS removed
- Architecture regression tests added at `tests/FullWorth.Web.Tests/FrontendArchitectureGuardTests.cs`
- the architecture allow-lists have been reduced continuously as violations are removed

## Next work

Continue with the remaining allow-list violations, preferably in small blocks.

### 1. Finish the remaining Purchase helpers

`features/purchase-articles-advanced-actions.js`

- remove direct BFF URL construction
- replace native `confirm()`
- replace remaining native `alert()` where practical
- use shared API / confirm / dialog / button primitives

`features/purchase-discount-actions.js`

- replace native `confirm()`
- use shared destructive action semantics

`features/purchase-receipt-source-review.js`

- replace direct BFF URL construction with the shared API URL builder

After each migration, remove the file from the relevant architecture allow-list.

### 2. Transactions

`features/transactions.js` remains an important direct-BFF exception. Move its direct BFF access onto the shared API client without changing transaction UX.

### 3. Remaining allow-list entries

Open `FrontendArchitectureGuardTests.cs` and work through the remaining entries one by one.

The allow-list must only shrink.

### 4. Continue reducing `app.js`

Move remaining feature-specific logic into the actual feature owner. `app.js` should end as bootstrapping/composition only.

### 5. CSS cleanup

After behavioral architecture is stable, continue splitting monolithic shared CSS into shared component/layout CSS and feature-owned CSS. Do not use visual theme files as structural repair layers.

## Validation after every block

- syntax-check changed JavaScript modules
- update architecture guard allow-lists immediately
- verify no new direct dialogs/BFF calls/observers/native confirms were introduced
- keep service-worker shell entries in sync when modules are renamed/removed
- check whether `main` moved; merge it into the cleanup branch before the next larger block

## Do not do yet

Do not perform the final Accounts migration until the user explicitly approves it after the separate Accounts UX work is finished.
