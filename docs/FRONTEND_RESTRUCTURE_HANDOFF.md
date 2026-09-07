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

Important: the earlier architecture cleanup is already merged. Current main has a core/ foundation. Evolve that existing core instead of creating a duplicate app/router/api/state stack.

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
- ordinary debit/list/contract values should normally be neutral; red is reserved mainly for genuine negative/problem states such as overdraft, budget exceeded, failed payment/sync or meaningful loss
- an analytical chart may still use a red expense series as a balanced legend; this must not make every ordinary expense red
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

This model has since been implemented in Analytics (see "Current FullWorth mismatches to fix" → "Analytics — shipped"). For reference, `cycleWindow()` maps:

```text
Woche    -> letzte 12 Wochen
Monat    -> letzte 12 Monate
Quartal  -> letzte 8 Quartale
Jahr     -> letzte 5 Jahre
```

That window is now used only as the chart's history preview, ending at the active bucket, rather than as the main number. The selector describes the **active bucket**, while the chart independently shows surrounding/history buckets. Any surface not yet migrated (e.g. Budgets) must adopt the same distinction.

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

This matches the simple mental model used by Finanzfluss mobile detail views; Analytics now implements it (prev/next steps one bucket) instead of the earlier FullWorth interpretation of `Monat` as a 12-month aggregate.

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

> Update: the analytics period semantics, the Dashboard net-worth preview and the normal-expense red-alarm have shipped (marked below). A later owner refactor (commits `5a614be`…`9f9893f`) also landed the shared navigation + event core, per-feature activate/unmount **lifecycle** (`core/feature-registry.js`), centralized route writes, the shared Dashboard↔Analytics period, and comparable-window budgets. Of the original structural goal, what remains is: **shrinking `app.js`** (still ~1366 lines with banking/accounts inline) and **finishing the `window.fwNavScope` → `core/navigation.js` migration** (3 callers left). The remaining data/model gaps (shared money-variant model, backend arbitrary-range averages, canonical merchant identity, category LIST slice) are still open. See `docs/OPEN_ITEMS.md`.

### Analytics — shipped

The selector now names the **active bucket** while the chart keeps the surrounding history window, so the KPIs no longer read as a 12-month aggregate:

- `ui/ux-kit.js::cycleWindow()` still returns an N-bucket window (Woche→12 weeks, Monat→12 months, Quartal→8 quarters, Jahr→5 years), but that window is now explicitly the chart-history preview that always **ends at the active bucket**; `offset` moves the active bucket — and the whole trailing window — by ONE bucket (prev/next = one month/quarter/…), and the navigator names the active period.
- `features/analytics.js::fillSpending()` no longer labels a window sum `Ausgaben gesamt`; its KPI is now `avgPerBucket()` rendered as `Ø Ausgaben / Monat` with a month-over-month trend.
- `fillInout()` now takes income/expense/net from the active bucket (the last history row), not the window total; the bar chart still shows the surrounding history.
- category and merchant cards now request the active-bucket range (`activeBucketRange`) for their totals/rows and use history only for comparison/trend.
- drills open the exact active bucket (`analyticsTxScope` scopes to `activeBucket`).

Still open here: the category overview LIST still slices the mixed parent+child list, and merchant grouping still keys off counterparty text — both tracked in the second-pass audit below.

### Dashboard

- SHIPPED: the net-worth widget is now a compact preview — current value + `miniSparkline` history + change over the range — that taps through to the full Wealth view (`ui/dashboard.js`).
- STILL OPEN: the default income/expense widget uses the current month, but its period configuration still has different semantics from Analytics. Both must converge on one shared PeriodState/PeriodPicker model.
- STILL OPEN: dashboard cards must deep-link with period/scope instead of starting a fresh unrelated view.

### Normal debits/expenses are visually over-signalled — CSS default shipped

SHIPPED (central CSS): `.amount.negative` is now neutral (`color:var(--text)` in `app.css`) and the spending chart line/area use `--accent`, so ordinary outflow is no longer red; red is reserved for genuine problem states.

STILL OPEN: this is enforced in CSS plus per-caller sign classes; there is still no shared money-variant model in `ui/money.js` (see "Ordinary-money color semantics need shared variants"), so callers keep picking `.negative`/`.positive` by sign. The remaining requirements below still hold as the target.

Historically, FullWorth used `negative` styling for many ordinary expense/debit values, including normal income/expense widgets and upcoming contract amounts.

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


## Second-pass audit — concrete gaps found on current main

This pass checked the current frontend and backend after the already-merged architecture cleanup, not only the earlier UX plan. These are implementation requirements.

### Architecture: continue the existing core instead of creating a parallel one

Current main already contains core/api.js, core/router.js, core/feature-registry.js, core/services.js, core/state.js and core/i18n.js. Use those as the base and evolve them. Do not create a second routing/API/state stack merely to match the earlier target tree.

The cleanup is still transitional:

- app.js is still large and owns shell/routing/feature wiring
- showView() still writes browser history itself
- core/router.js maps/writes URLs but does not own full route transition + page cleanup
- core/feature-registry.js provides refresh callbacks, not a mount/unmount lifecycle
- window.fwNavScope and other transitional globals still exist
- Accounts still has protected compatibility code and remains subject to the separate Accounts migration constraint

The target remains one router, explicit feature lifecycle/cleanup, no cross-feature DOM repair and no navigation globals.

### One period model must separate four different things

The finance UI needs one shared PeriodState concept:

    grain: week | month | quarter | year
    active: exact selected bucket (from/to/label/isCurrent/isComplete)
    preview: number of history buckets shown in the chart
    comparison: previous-period | same-elapsed-previous | trailing-average | none
    scope: account/group/category/merchant/direction/...

The current bug exists because granularity, active period and chart-history window are treated as the same range.

The backend should return finance semantics rather than making the browser derive them:

    active      = exact selected bucket totals
    history[]   = chart buckets
    previous    = explicit comparison bucket
    average     = explicit average over completed prior buckets
    metadata    = grain, sample count, completeness and scope

### Running periods are not completed periods

A running September is not equivalent to a completed August.

For a current/running bucket:

- primary value may be month-to-date/week-to-date/etc., but the UI must make that clear where relevant
- trailing averages use completed prior buckets and exclude the running partial bucket
- previous-period comparison must use one documented policy consistently: either same elapsed portion of the prior bucket or the prior complete bucket
- historical buckets are complete periods

For the Finanzguru-style monthly average specifically, the official behavior is the previous 12 months excluding the current month, divided by 12.

If fewer complete historical buckets exist, do not silently label the result as a 12-period average. Return sampleCount/coverage metadata and label the available-history average, or omit it.

### Backend range endpoints still have semantic gaps

Current backend behavior is inconsistent:

- month-specific category analytics already has Average3/Average6/Average12 (`CategoryAnalyticsService.CategorySpendForUserAsync`)
- arbitrary-range category analytics currently returns those average fields as zero (`CategoryAnalyticsService.CategorySpendForRangeForUserAsync` hardcodes Average3/6/12 to `0m`) — still open
- merchant arbitrary-range analytics compares with an equal-length previous range but has no trailing per-period average
- analytics overview returns totals over the whole requested from/to range plus byPeriod

Therefore Week/Quarter/Year averages and comparisons must be backend-supported. Do not compute financial averages ad hoc in analytics.js.

### Category overview still mixes hierarchy levels in the LIST

The backend returns parent and child category rows. The card's **total and donut are now root-only and disjoint** (`features/analytics.js::fillCategory` sums `cats.filter(c => !c.parentId)`, and `categoryDonut` renders roots only) — that part is fixed. But the visible **list** still takes `cats.slice(0, 6)` from the combined parent+child list, so a parent and one of its children can still appear together and the listed rows no longer sum to the donut/total. The card is therefore still internally inconsistent, now between its (root-only) total and its (mixed-level) rows.

⚠ Needs decision: the shipped card pairs a root-only donut/total with a top-6 list that can mix parent and child rows. Either the overview list should be filtered to root categories only (matching the donut and the "Required flow" below), or the list is intentionally the largest individual categories at any level and the total/donut should be relabelled to match. This is a product call — do not silently pick one.

Required flow:

    category overview
    -> root/main categories only
    -> tap root category
    -> subcategory detail
    -> tap "Gesamt" for the root subtree OR a specific subcategory
    -> matching transactions

Rules:

- root overview totals are disjoint
- a parent includes descendants exactly once
- donut, list and total use the same active period and hierarchy level
- EUR/% is a display choice, not different calculation logic
- keep includeDescendants=true for the final transaction drill-down

### Merchant grouping and drill-down need the same stable identity

Current merchant analytics is based on normalized counterparty text. Transaction DTOs can separately resolve MerchantId/MerchantDisplayName from the merchant registry and aliases, while the analytics drill-down still filters transactions via merchant text substring.

That can make the card aggregate differ from the transaction list opened after tapping it.

Preferred end state:

- analytics groups by canonical merchant identity where available
- analytics result exposes merchantId + display name
- transaction query accepts merchantId
- aliases resolve server-side
- text/counterparty filtering remains a search fallback, not the primary identity contract

### Dashboard and Analytics currently use different period systems

Dashboard currently has:

- a default current-month income/expense snapshot from api/analytics/dashboard
- a separate configurable 7d/month/quarter/year/1y/all period helper

Analytics separately uses cycleWindow() with W/M/Q/Y history windows.

These must converge on one shared period/scope contract. Dashboard cards are previews of the same domain calculations, not separate calculation systems.

Drill-down from Dashboard should preserve active period and relevant scope.

### Dashboard wealth preview — shipped

The full Wealth page already has a useful hierarchy: current net worth, history, allocation, liabilities, emergency fund and management/details.

The Dashboard wealth card is now the intended compact preview (`ui/dashboard.js`):

    current net worth
    small sparkline/history (miniSparkline over api/net-worth/history)
    change over the preview range
    tap -> full Wealth

Wealth management controls are not duplicated into Dashboard.

### Mixed budget cycles must not be blindly added together

The backend correctly evaluates each budget in its own current period, including weekly, biweekly, monthly, quarterly, yearly, pay-cycle and custom cycles.

The frontend budget page currently sums all budget amounts/spend into one totalBudgeted / totalSpent / remaining headline.

That is mathematically misleading when visible budgets use different active period lengths.

Required rule:

- only aggregate when the visible budgets share a compatible active window
- otherwise show per-budget status, group compatible periods, or explicitly normalize to a common cadence and label that normalization
- never present weekly + monthly + quarterly + salary-cycle values as one ordinary period total
- carry-over remains part of each budget's own active period

Budget red is appropriate after a budget is exceeded; amber can signal near-limit. Ordinary contributing expense rows are not danger just because their sign is negative.

### Contracts: keep normalized summary and real cadence separate

The backend already exposes monthlyEquivalent and annualized values. Preserve that.

- summary may use monthly-equivalent recurring cost
- each row/detail keeps the real amount and cadence
- a yearly contract contributes one twelfth to a monthly-equivalent summary but is still visibly yearly
- normal recurring cost is not a danger state

### Wealth uses point-in-time math, unlike cash-flow analytics

Net worth is a stock/point-in-time measure and must never be summed across months/quarters/years.

Changing the wealth history range changes the graph and start/end comparison. It does not change the meaning of the headline current net-worth value.

Liabilities/debt may remain visually distinct from assets. This is different from making every ordinary debit transaction red.

### Ordinary-money color semantics need shared variants

Partly addressed: the central CSS default no longer reds normal outflow (`.amount.negative` is neutral, charts use `--accent`). Still missing: a shared money display model. `ui/money.js` today only formats/masks values (`money`/`converted`/`percent`) — it has no variant concept, so callers and CSS still switch on sign (`.negative`/`.positive`) rather than intent, and some surfaces still lean on `negative` styling for ordinary expenses, contract costs and debit rows.

Use a shared money display model such as:

    neutral
    income
    warning
    danger
    debt
    muted

Default behavior:

- ordinary debit transaction = neutral/primary
- transfer = neutral
- normal contract cost = neutral/primary
- ordinary spending KPI = neutral/primary
- actual warning/problem state = warning/danger
- positive income may use subtle positive styling

A balanced income-vs-expense chart may deliberately use two series colors (Finanzfluss, for example, uses green income and red expense bars). That is an analytical legend, not a rule that every negative amount in the application should be red.

### Transfers, refunds, pending and excluded bookings must reconcile everywhere

Current backend already has important calculation rules that the refactor must preserve:

- transfers are excluded from ordinary income/expense analytics
- ignored/excluded-from-statistics bookings are excluded
- pending bookings are not silently mixed into booked totals
- linked refunds reduce the original expense allocation rather than becoming ordinary income

Dashboard, Analytics, category/merchant cards and their transaction drill-downs should reconcile to those same rules. If a detail list intentionally uses a broader scope than its KPI, explain the difference.

### Existing tests do not catch the main finance semantics bug

Current finance UX baseline tests prove that W/M/Q/Y, byPeriod and drill-down tokens exist. They do not prove what those periods mean, so the current "Monat = 12-month aggregate" behavior can still pass.

Add regression coverage for:

- Monat headline = selected month, not sum of chart history
- preview history can show several months independently of the active KPI
- monthly average excludes the current partial month
- W/Q/Y comparisons/averages use completed prior buckets
- prev/next moves one active bucket, not one entire preview window
- Dashboard and Analytics use the same period contract
- category overview contains root categories only
- category parent totals include descendants exactly once
- merchant aggregate reconciles with merchant drill-down
- mixed-cycle budgets are not blindly summed
- net worth is point-in-time and never summed across history
- ordinary debit/contract/spending rows do not automatically receive danger styling
- actual over-budget/overdraft/sync-error states still receive semantic warning/danger styling

Backend integration tests should validate numerical truth; Web tests should validate period/scope/navigation/presentation contracts.

### First-screen information hierarchy

The product should answer a user question before exposing management controls:

    Overview:
      What do I have, what happened, what is upcoming?

    Analytics:
      What happened in this selected period?
      How does it compare?
      Where did the money go?

    Contracts:
      What do recurring commitments cost?
      What is due next?

    Budgets:
      How much of each active budget period is used/left?

    Wealth:
      What am I worth now?
      How did it develop?
      How is it allocated?

Editing, rules, imports, advanced analyses and management stay available one level deeper.


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
