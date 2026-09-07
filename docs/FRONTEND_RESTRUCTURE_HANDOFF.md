# Frontend restructuring handoff

Status: **handoff for next implementation agent**  
Repository: `Juloc/FullWorth`  
Target: `main`

## Goal

Do not add another UX patch layer on top of the current frontend.

Restructure `FullWorth.Web` so future changes to Accounts, Transactions, Contracts, Analytics and Wealth are implemented through clear feature modules and shared UI primitives instead of globals, DOM decorators, duplicated dialogs/sheets, CSS overrides and navigation workarounds.

The product direction remains: simple, consumer-finance UX inspired by the supplied Finanzguru references, while keeping FullWorth branding and FullWorth-specific features.

Existing references:

- `docs/SIMPLE_FINANCE_APP_UX_REWORK_PLAN.md`
- `docs/SIMPLE_FINANCE_APP_UX_GAP_CLOSURE_PLAN.md`
- `docs/UI_UX_SPEC.md`

## Current behavior that must not regress

Already present on `main`:

- account -> transactions for that account
- account group -> transactions for all accounts in that group
- expand/collapse is separate from group drill-down
- transaction scope is URL-backed
- group scope is resolved server-side
- category descendant filtering exists
- transactions are grouped by booking date
- transaction identity uses brand/logo -> category icon -> fallback
- analytics supports Week / Month / Quarter / Year
- analytics cards use one selected period
- contracts expose monthly/annual normalized cost from backend
- contracts already support filter/sort state
- wealth already contains trend, allocation and optional emergency-fund concepts
- mobile primary navigation is already reduced to five destinations

## Target frontend structure

```text
wwwroot/
  app/
    bootstrap.js
    router.js
    app-context.js
    api-client.js
    event-bus.js
    routes.js

  shell/
    app-shell.js
    top-bar.js
    bottom-nav.js
    sidebar.js
    more-menu.js

  ui/
    button.js
    card.js
    list-row.js
    money.js
    identity-icon.js
    modal.js
    bottom-sheet.js
    filter-sheet.js
    period-selector.js
    loading.js
    empty-state.js
    chart.js

  features/
    accounts/
    transactions/
    contracts/
    analytics/
    wealth/
    budgets/
    purchases/
    categories/
    settings/

  styles/
    tokens.css
    reset.css
    shell.css
    components.css
    responsive.css
    features/

  locales/
```

This is the target direction, not a requirement to move every file in one commit.

## Architecture rules

### 1. Shrink `app.js`

`app.js` must stop owning routing, API access, account rendering, navigation and feature-specific logic at the same time.

Final bootstrap responsibility should be roughly:

```text
load session
-> create AppContext
-> start shell
-> start router
```

No feature rendering logic should remain in the bootstrap layer.

### 2. One page lifecycle

Every routed page should follow one lifecycle contract, for example:

```js
export async function mount(ctx, route) {
  // render + bind
  return () => {
    // cleanup listeners/resources
  };
}
```

Avoid:

- DOM mutation observers used to decorate other feature output
- `dataset.*Bound` flags as lifecycle management
- retry/setTimeout loops waiting for another feature to render
- registering global listeners without cleanup
- Feature A rewriting Feature B's DOM

### 3. Router is the only navigation API

Replace patterns such as:

- clicking nav buttons from code
- calling page internals directly
- `window.fwNavScope`
- scattered `history.pushState`

with one router API, e.g.:

```js
router.go('/transactions', { accountId });
router.go('/transactions', { groupId });
```

The router owns URL, back/forward handling and route state.

### 4. Shared UI primitives only

Do not let every feature create its own base components.

Central shared primitives should cover at least:

- Modal
- BottomSheet
- FilterSheet
- SectionCard
- ListRow
- MoneyValue
- IdentityIcon
- PeriodSelector
- LoadingSkeleton
- EmptyState
- Chart wrapper

Transactions, Contracts and Analytics must reuse the same primitives.

### 5. Central identity system

Use one identity resolver everywhere:

```text
brand/logo
-> category icon
-> generic fallback
```

Transactions, contracts, recent bookings and related widgets must not implement independent icon logic.

### 6. Feature API modules

Views should not contain raw backend URLs everywhere.

Use modules such as:

```text
features/transactions/api.js
features/contracts/api.js
features/analytics/api.js
```

Example:

```js
transactionsApi.list({
  accountId,
  groupId,
  from,
  to,
  categoryId
});
```

Frontend modules may format requests, but backend business logic remains backend-owned.

### 7. Do not duplicate finance logic in JavaScript

Backend remains source of truth for:

- contract monthly/annual normalization
- authorization
- account-group transaction scope
- category descendants
- transfer logic
- analytics calculations
- currency calculations
- merchant normalization

The browser renders backend DTOs; it does not recalculate finance truth.

### 8. CSS must become layered, not patched

Target CSS ownership:

```text
tokens.css       colors, spacing, radius, typography, sizes
shell.css        app shell/navigation/layout
components.css   button/card/row/sheet/dialog/badge
responsive.css   central breakpoints
features/*.css   feature-only rules
```

Rule: do not fix a component by adding another increasingly-specific override. Correct the owning component/style.

Remove old override/fix layers only after the migrated feature is verified.

## Suggested migration order

### Phase 1 — foundation

Create/migrate:

- AppContext
- API client
- Router
- App shell
- shared Modal
- shared BottomSheet
- shared UI tokens/components

Do not change finance behavior during this phase.

### Phase 2 — Transactions as reference feature

Migrate Transactions completely first.

It should become the example for:

- route state
- feature API layer
- mount/cleanup
- ListRow
- IdentityIcon
- date groups
- FilterSheet
- detail view

Keep existing scoped account/group behavior.

### Phase 3 — Contracts and Analytics

Migrate both onto the same architecture.

Contracts:

- shared identity
- shared sort/filter sheet
- shared rows/cards
- no duplicated cadence calculation

Analytics:

- shared PeriodSelector
- simple card-first home
- Week / Month / Quarter / Year remains global
- advanced/custom builder remains below normal analysis

### Phase 4 — Wealth, Budgets, Purchases

Move them to the same primitives and lifecycle.

Wealth first viewport should remain focused on:

1. trend
2. allocation
3. emergency fund when configured
4. management/details below

### Phase 5 — Accounts

Accounts currently have parallel UX work.

During restructuring:

- migrate Accounts technically into the new architecture
- use the shared router/API/components
- do not redesign the visible Accounts UX without confirmation
- preserve current account/group behavior

### Phase 6 — remove legacy code

Only after migrated paths work and tests pass, remove:

- old app.js feature blocks
- Window globals
- DOM decorators
- mutation-observer workarounds
- duplicated dialogs
- duplicated bottom sheets
- old CSS override/fix layers
- dead legacy markup/styles

## Hard rules after migration

New feature code must not:

- create new `window.*` APIs
- navigate by triggering another control's `.click()`
- mutate another feature's DOM
- create another base dialog/sheet implementation
- duplicate backend finance calculations
- add global CSS overrides to repair page-specific bugs
- register listeners without cleanup
- grow `app.js` with new feature logic

## UX direction after the structural work

Once the foundation is stable, continue the simple finance-app presentation:

- Overview is the daily money hub
- account/group drill-down goes directly to bookings
- mobile booking rows are visually simple: identity, merchant, category/context, amount
- analytics starts with understandable cards, not a builder
- contracts are a compact consumer list with filters/sorts one level deeper
- wealth starts with trend/allocation/reserve before management tools
- advanced/admin controls stay available but one level deeper
- FullWorth branding remains distinct


## Important UX corrections to preserve during the restructure

These points are product requirements, not optional visual polish.

### Analytics/statistics period semantics

The current analytics/statistics presentation must not make a trailing 12-month total look like a normal "monthly" value.

Rules:

- `Monat` means the selected/current month as the primary period.
- When a 12-month context is shown on a monthly-oriented card, the primary comparison value should normally be the **monthly average**, not the sum of all 12 months.
- A yearly total may still exist in detail views, but it must be explicitly labelled as a yearly/12-month total.
- The same rule applies consistently to income, expenses, contracts/fixed costs and category/merchant summaries where a monthly interpretation is expected.
- Do not mix "selected month", "last 12 months" and "monthly average" without explicit labels.

Interaction:

- tapping a statistic/card should open the corresponding detail for the selected period/scope, e.g. the selected month and its matching bookings
- do not send the user first into the advanced/custom builder
- period and scope must survive the drill-down

Acceptance example:

```text
Monat selected
-> card shows September
-> optional comparison: monthly average of trailing 12 months
-> tap card/category/merchant
-> detail opens September with matching bookings
```

### Wealth preview/drill-down

Where wealth is referenced outside the full Wealth page, use a compact preview rather than exposing management UI.

A useful preview can contain:

- current net worth
- small trend/sparkline
- period change
- clear tap/click target to open the full Wealth view

The full Wealth page then contains trend, allocation, emergency fund and details/management.

### Normal spending is not an error state

Normal expenses must **not** be visually treated as danger.

Rules:

- do not make "Ausgaben" the dominant red element of Overview or Analytics
- red is reserved for genuinely negative/problem states such as overdraft, budget exceeded, failed payment/sync or meaningful loss
- normal spending should use neutral text/card styling or the normal FullWorth accent/category treatment
- income and expenses should have balanced visual hierarchy
- avoid a large red expense hero that makes the whole app feel like an alert screen

The target is the calm, neutral consumer-finance hierarchy visible in the supplied Finanzguru references, while keeping FullWorth's own colors and branding.



## Research-backed aggregation and grouping model

Reference behavior checked against the supplied Finanzguru screenshots plus current Finanzguru/Finanzfluss product/help material.

The important product distinction is:

```text
selected period
!=
history shown in the preview chart
!=
average/comparison value
```

FullWorth currently mixes these concepts in several places. In particular, `cycleWindow()` currently maps:

```text
Woche    -> letzte 12 Wochen
Monat    -> letzte 12 Monate
Quartal  -> letzte 8 Quartale
Jahr     -> letzte 5 Jahre
```

and then several cards use the aggregate over that entire history window as the main number. This is the core semantics bug. The selector must describe the **active bucket**, while a chart may independently show surrounding/history buckets.

### Canonical period model

Use one reusable period state:

```text
granularity: week | month | quarter | year
activePeriod: one concrete week/month/quarter/year
previewWindow: N buckets around/before the active period
comparison: previous bucket | average of trailing N buckets | none
scope: accounts/groups/categories/merchants/etc.
```

Examples:

| Selector | Primary value means | Preview can show | Optional comparison |
| --- | --- | --- | --- |
| Woche | selected ISO week | last 12 weeks | Ø/week or previous week |
| Monat | selected calendar month | last 6-12 months | Ø/month over trailing 12 months or previous month |
| Quartal | selected calendar quarter | last 6-8 quarters | Ø/quarter or previous quarter |
| Jahr | selected calendar year | last 5 years | Ø/year or previous year |

Do **not** make the primary value the sum of all buckets currently visible in the preview chart unless the UI explicitly says e.g. `Summe letzte 12 Monate`.

Prev/next navigation moves by **one active bucket**:

```text
Monat: September -> August -> Juli
Quartal: Q3 -> Q2 -> Q1
Jahr: 2026 -> 2025 -> 2024
Woche: KW 36 -> KW 35
```

This matches the simple mental model used by Finanzfluss mobile detail views and avoids the current FullWorth interpretation of `Monat` as a 12-month aggregate.

### Development/trend cards

A development card has two layers:

1. **history preview**: multiple buckets in a chart
2. **main KPI**: either the active bucket or an explicitly labelled average per bucket

For a Finanzguru-style spending-development card, this is valid:

```text
chart: last 12 months
main KPI: Ø 2.318 € / Monat
trend: +36 € / Monat
tap a bar: open that exact month
```

It is **not** valid to show:

```text
selector: Monat
chart: 12 months
main KPI: 27.816 €
```

without clearly labelling that figure as the total over 12 months.

### Income / expenses / saldo

For the active period show:

- income for the active bucket
- expenses for the active bucket
- saldo for the active bucket
- optional saved amount / savings rate when reliable

The chart can show previous buckets for context.

Drill-down:

- tap income -> income bookings for the active bucket
- tap expenses -> expense bookings for the active bucket
- tap a historical bar -> bookings for that bar's exact bucket

Do not make all visible history buckets the detail scope by default.

### Categories

Category analysis is always scoped to **one active period** first.

Structure:

```text
active period total
-> root/main categories
-> tap category
-> subcategories
-> tap "Gesamt" or a subcategory
-> matching transactions
```

A category parent total includes descendants exactly once.

The preview/home card may show a donut plus the largest root categories, but its sum and rows must refer to the same active period.

Support:

- expenses / income switch where useful
- EUR / percent switch in detail
- account/group scope
- period navigation

Do not mix a 12-month category total with a `Monat` label.

### Merchants / recipients

Same period semantics as categories:

```text
active period
-> merchants/recipients grouped by normalized merchant
-> amount + transaction count + optional average booking value
-> tap merchant
-> matching transactions in the same active period
```

A historical merchant trend may exist in detail, but the list itself must not silently aggregate the entire preview history.

### Accounts and account groups

Keep the existing drill logic:

```text
account group -> combined bookings of its accounts
account       -> bookings of that account
```

Group balances are point-in-time sums, not transaction-period sums.

Any analytics account/group filter must use the same active-period model above.

### Contracts

Contract summaries use a normalized cadence, not the raw sum of upcoming payments.

Primary summary:

```text
Ø monthly contract cost
optional annualized total
```

Individual contracts retain their real cadence.

Useful grouping/sorting dimensions are independent of the time selector:

- account
- contract type
- category
- cadence
- next due date
- monthly equivalent
- annual cost
- provider/name

A yearly contract should contribute `annual amount / 12` to the monthly-equivalent summary, while still being shown as yearly in its row/detail.

### Budgets

Budgets are evaluated against **their own active budget period**.

Default mobile interpretation:

- selected/current month (or salary-cycle period)
- spent in that period
- remaining in that period
- forecast for that period

Historical bars may show previous budget periods, but do not add them into the current-period KPI.

### Wealth / net worth

Net worth is a **point-in-time value**, never a sum across periods.

Overview/dashboard should use a compact preview:

```text
current net worth
+ small history line/sparkline
+ change over selected preview range
-> tap -> full Wealth view
```

Full Wealth detail:

- current net worth
- history
- allocation by account / investment / real estate / other assets
- liabilities separately
- emergency fund when configured

Changing the time range changes the history/comparison, not the meaning of the current net-worth number.

### Dashboard/overview cards

Dashboard cards are previews, not separate calculation systems.

They should always reuse the same domain query/period model as detail pages.

Examples:

- Einnahmen & Ausgaben -> active month values + small history preview
- Vermögen -> current value + sparkline + change
- Verträge -> monthly equivalent + next due preview
- Budgets -> current budget period status
- recent bookings -> actual latest bookings

Tap the card/title/row to enter the corresponding detail with the same scope/period where applicable.

## Current FullWorth mismatches to fix during migration

These are known issues in the current frontend and must not be preserved just because they exist on `main`.

### Analytics

- `ui/ux-kit.js::cycleWindow()` currently treats `month` as 12 months, `quarter` as 8 quarters and `year` as 5 years.
- `features/analytics.js::fillSpending()` uses `overview.expenses` for the entire history window and labels it `Ausgaben gesamt`.
- `fillInout()` likewise uses income/expense/net over the entire history window as its main KPI.
- category and merchant cards currently receive the same broad history window; for the simple card view they should instead represent the active bucket, with history used only for comparison/trend.
- the current global cycle therefore conflates **granularity**, **active period** and **preview history**.

Refactor these into separate concepts rather than patching labels.

### Dashboard

- the net-worth widget currently shows only a number plus assets/liabilities; add the compact history/change preview.
- the default income/expense widget uses the current month, but the period configuration has different semantics from Analytics. Both must use one shared PeriodState/PeriodPicker model.
- dashboard cards must deep-link with period/scope instead of starting a fresh unrelated view.

### Normal debits/expenses are visually over-signalled

Current FullWorth uses `negative` styling for many ordinary expense/debit values, including normal income/expense widgets and upcoming contract amounts.

For the consumer-finance default:

- ordinary outgoing bookings are **not an error**
- ordinary contract costs are **not an error**
- ordinary spending totals are **not an error**
- use normal FullWorth primary/neutral text and chart colors for normal outflow
- a Finanzguru-style spending-development chart can use the main FullWorth accent for spending
- green may be used sparingly for actual income/gain/positive states
- red is reserved for genuine problem/risk states: overdraft, budget exceeded, failed/late payment, sync error, loss/warning where red is semantically required

Transaction-list debit amounts should normally be neutral/primary, not red merely because their sign is negative.

This visual semantic rule must be applied centrally in `MoneyValue`/amount variants rather than fixed page-by-page.

## Reference behavior used for this decision

Finanzfluss mobile uses a period selector for `Monat / Quartal / Jahr`, while its overview charts show several historical buckets; the KPIs in detail refer to the selected concrete period, and footer arrows move one period at a time. Category drill-down proceeds from main category to subcategory to matching transactions.

Finanzguru similarly separates overview/trend history from the normalized KPI on development cards (for example an average `€/Monat` across a multi-month development chart), and its analysis system supports grouping by main category, subcategory or recipient with drill-through to filtered bookings.

Do not copy either product's branding or exact visual assets. Copy the **information semantics and interaction clarity**.

## Definition of done for this restructuring

The restructuring is complete when:

- routing is centralized
- page lifecycle/cleanup is explicit
- feature pages no longer depend on DOM decoration from unrelated modules
- Transactions, Contracts, Analytics and Wealth use shared UI primitives
- feature API access is separated from rendering
- finance calculations are not duplicated in frontend
- legacy globals/workarounds have been removed where migrated
- light/dark, DE/EN, mobile/desktop and accessibility still work
- existing account/group transaction scoping still works
- future UX changes can be made in feature modules/components without adding another workaround layer
