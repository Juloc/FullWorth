# FullWorth / FullWorth — MVP UI/UX specification

Status: **binding implementation contract for the authenticated application UI**.

This document defines the MVP app experience in enough detail that an implementation agent or junior developer should not need to invent product behavior. When UI behavior in this document conflicts with generic product notes, this document is the more specific UI contract. Product/security requirements in `docs/PRODUCT_DECISIONS.md` and `docs/BANKING_SAFETY.md` still remain authoritative.

The public landing page is intentionally not specified here. Plan it after the authenticated app has been implemented and tested.

---

# 1. Product direction

The MVP must already feel like a complete personal-finance application, not an admin dashboard. It should cover the everyday quality level expected from products such as Finanzguru, Finanzblick, Wallet and Finanzfluss/Copilot while keeping FullWorth's own flexible data model.

Core principles:

1. **Useful by default, configurable by choice.** A new user gets a strong dashboard and sensible defaults without configuring anything.
2. **Everything important remains editable.** Default categories, account groups, dashboard widgets, budgets and rules are starting points, not locked system objects.
3. **Financial math must remain explainable.** Never show a percentage that changes unpredictably because of deposits, withdrawals, buys or sells.
4. **Complexity is progressive.** Common actions are one or two taps/clicks away; advanced options live in edit/configuration surfaces.
5. **Mobile is not a compressed desktop.** Desktop uses a grid; mobile defaults to a single ordered widget list.
6. **Privacy is a first-class UI mode.** Users can safely show the screen without exposing values.
7. **Gamification is not part of the base MVP UI.** The base component/data architecture must allow a later optional Game Mode without changing finance calculations or persistence semantics.
8. **FullWorth Space ready, single-space UI for MVP.** The app operates against one default FullWorth Space. Do not expose a space switcher yet, but no UI/API implementation may assume that only one space can ever exist.

---

# 2. MVP scope and deferred scope

## 2.1 In MVP

- Dashboard / Overview with configurable widgets
- Accounts and account groups
- Transactions including manual transactions, splits, refunds and transfer correction
- Contracts / recurring costs
- Budgets
- Net worth, assets, liabilities and investments when data is available
- Analytics and configurable charts
- Purchases / receipts and receipt-to-transaction linking
- Hierarchical editable categories
- Categorization rules
- Global search
- Push-notification preferences for supported notification types
- DE/EN localization
- System/Light/Dark themes
- Multi-currency display and analytics
- Privacy / anonymized mode
- Responsive desktop/tablet/mobile/PWA behavior

## 2.2 Explicitly deferred, but architecture must not block it

- Public marketing/landing page
- Optional Game Mode / gamification layer
- Public social/community features
- Pre-shopping checklist/planner as a full separate shopping-list product
- Advanced portfolio benchmarking by market index/sector/region if the necessary investment data is not yet available
- Automatic property valuation providers
- Visible multi-space switcher and household management UI beyond what current security work requires
- Highly custom visual theme editor; MVP uses defined themes/tokens

---

# 3. App shell and navigation

## 3.1 Desktop >= 1024 px

Use a persistent left sidebar and a top page header.

Sidebar:

- width expanded: `248px`
- width collapsed: `72px`
- user can collapse it
- product mark/name at top
- primary navigation in the middle
- Settings/profile at bottom

Default desktop navigation order:

1. Overview
2. Transactions
3. Accounts
4. Budgets
5. Contracts
6. Net worth
7. Analytics
8. Purchases
9. Categories
10. Rules
11. Notifications
12. Settings

Do not hide major desktop sections behind a generic hamburger menu.

Top page header contains:

- page title
- optional page-level context/period selector
- global search button/input
- privacy-mode toggle
- notification indicator
- one primary contextual action, for example `Add transaction`, `Add widget`, `Add budget`

## 3.2 Mobile < 768 px

Use a fixed bottom navigation with exactly five visible destinations.

Default:

1. Overview
2. Transactions
3. Budgets
4. Net worth
5. More

`More` is fixed. The first four slots can be customized in Settings from the set of app sections. All sections remain reachable from `More`, including Overview if the user removes it from the first four slots.

Use a compact top bar with:

- current page title
- privacy toggle
- search
- contextual overflow/action where required

For creation actions use one floating or header `+` action that opens a short action menu. Do not place multiple floating buttons on one screen.

Quick actions:

- Manual transaction
- Scan receipt
- Add asset
- Add contract

Only show actions the current user is authorized to perform.

## 3.3 Tablet 768–1023 px

Use the mobile navigation model by default with wider content cards. Do not force the full desktop sidebar until `1024px`.

---

# 4. Global visual system

The first implementation should be clean, neutral and modern. Do not bake gamification into spacing, hierarchy or data components.

## 4.1 Layout tokens

- base spacing unit: `4px`
- normal component spacing: `8 / 12 / 16 / 24 / 32px`
- page horizontal padding desktop: `24px`
- page horizontal padding mobile: `16px`
- dashboard/card gap desktop: `16px`
- dashboard/card gap mobile: `12px`
- card radius: `16px`
- input/button radius: `10px`
- compact badge/pill radius: full pill
- maximum main content width: `1600px`; center content on very wide displays

## 4.2 Typography

Use the system UI font stack. Do not add a downloadable webfont solely for the MVP.

Hierarchy:

- page title: 28–32px desktop, 24px mobile, semibold
- card title: 14–16px, semibold
- primary financial value: 24–32px, semibold
- normal body: 14–16px
- supporting/meta: 12–13px

Use tabular numerals for financial values where supported.

## 4.3 Color semantics

Never rely on color alone.

Semantic colors:

- positive: green semantic token
- negative: red semantic token
- warning: amber semantic token
- information/accent: theme accent token
- neutral: surface/text tokens

Do not hard-code semantic meaning directly into component CSS. Use variables/tokens so later themes can replace visuals without changing components.

## 4.4 Themes

MVP theme selector:

- System
- Light
- Dark

The component system must use semantic tokens. Additional branded themes such as Dawn/Aurora/Sunset/Pink can be added later without changing component markup or financial logic.

---

# 5. Privacy / anonymized mode

Privacy mode is global and must apply to every authenticated page, dialog, tooltip and chart.

The normal toggle has two states:

- Off
- Privacy on

When Privacy is on:

- replace monetary values with a stable mask such as `•••• €`; do not rely only on CSS blur
- mask investment gains/losses and percentages
- mask account numbers/IBAN except optional final four characters
- mask totals inside chart tooltips and axis labels
- keep chart shapes visible but remove exact numeric axis labels
- merchant names and category names stay visible by default

Settings additionally expose `Strict share mode`:

- includes all Privacy behavior
- also masks personal names, account custom names if marked private, contract identifiers and free-text notes
- disables temporary hover-to-reveal

Implementation rule: use one shared `MoneyValue`/`SensitiveValue` rendering path. Never implement privacy by manually hiding values per page.

---

# 6. Dashboard / Overview

The Dashboard is the default route after login.

## 6.1 Dashboard header

Desktop header:

- title `Overview`
- period context only when relevant; do not apply one global period to account balances
- `Add widget`
- `Edit dashboard`
- privacy toggle remains in global header

Mobile header:

- title
- privacy toggle
- overflow menu containing `Add widget`, `Edit layout`, `Reset layout`

Do not show a decorative greeting that consumes vertical space.

## 6.2 Desktop grid

Use a **12-column responsive grid**. This is a layout grid, not pixel-free canvas placement.

Allowed widget widths:

- `3` columns: compact
- `4` columns: small
- `6` columns: half
- `8` columns: wide
- `12` columns: full

Allowed height presets:

- compact
- normal
- tall

Widgets are draggable and resizable only in Edit mode. Normal mode must not show drag handles.

Persist widget order, width, height and configuration per user.

## 6.3 Mobile layout

Default mobile layout is a single ordered list of widgets. Each widget is full width.

Dashboard layout preference:

- `Shared layout`: desktop configuration determines widget set/order; mobile renders the same widgets as a list.
- `Separate layouts`: user maintains an independent mobile widget set/order and desktop grid.

Default is `Shared layout`.

Do not attempt to preserve desktop x/y positions on mobile.

## 6.4 Default dashboard layout

A new FullWorth Space gets these widgets in this order.

Desktop:

Row 1:
- Net worth summary: width 8, normal
- Available until next income: width 4, normal

Row 2:
- Account overview: width 12, tall

Row 3:
- Income vs expenses: width 6, normal
- Budget focus: width 6, normal

Row 4:
- Net worth / portfolio trend: width 8, normal
- Upcoming contracts: width 4, normal

Row 5:
- Recent transactions: width 8, normal
- Alerts & actions: width 4, normal

Purchases/receipt widget is not on the default dashboard but is available in the widget library.

Mobile uses the same initial order as a list.

## 6.5 Dashboard edit mode

Top edit bar:

- `Done`
- `Add widget`
- `Reset to default`

Per widget:

- drag handle
- resize control on desktop
- configure
- duplicate
- remove

Removing a widget removes only the dashboard instance, never underlying data.

Reset must show a confirmation and reset layout/widgets only. It must not reset financial data or preferences outside Dashboard.

---

# 7. Dashboard widget contract

Every widget instance has:

```text
Id
WidgetType
TitleOverride?
LayoutDesktop
LayoutMobileOrder
DataScope
Period
Comparison
Visualization
ForecastMode
DisplayOptions
```

All widget types must support:

- loading state
- empty state
- error state with retry
- privacy mode
- light/dark themes
- DE/EN
- authorization-scoped data

## 7.1 Data scope selector

Where relevant, configuration offers:

Accounts:
- all accounts
- account groups
- selected accounts

Categories:
- all categories
- selected categories
- optionally include descendants

Budgets:
- all budgets
- selected budgets

Investments:
- whole portfolio
- asset classes, e.g. ETF, stock, crypto
- selected securities/assets

Contracts:
- all
- selected contract types
- selected contracts

Never silently broaden an explicitly selected scope when new accounts/categories/assets are created. `All` is dynamic; `Selected` remains an explicit list.

## 7.2 Period selector

Reusable presets:

- Today
- 7 days
- This week
- This month
- This quarter
- This year
- 1 year
- 5 years
- 10 years
- All time
- Custom

Charts that aggregate data additionally expose granularity when meaningful:

- day
- week
- month
- quarter
- year

Invalid combinations are disabled rather than producing unreadable charts.

## 7.3 Visualization selector

Supported MVP chart forms:

- line
- area
- bar
- stacked bar
- horizontal bar
- donut
- compact sparkline

Do not use pie/donut for time series.

## 7.4 Forecast display

Forecast is always visually distinct from actual values:

- actual series: solid
- forecast series: dashed and/or lower-emphasis fill
- show `Forecast` label in legend/tooltip
- tooltip states whether value is actual, known future transaction or model estimate

Forecasts must never be presented as guaranteed outcomes.

---

# 8. Widget catalog

## 8.1 Account overview

This is the core default widget and should feel similar in usefulness to Finanzguru's grouped account overview.

Default automatic groups:

- Current accounts
- Savings
- Credit cards
- Cash/manual accounts
- Investments
- Other

Users can:

- create custom groups
- rename groups
- reorder groups
- move accounts between groups
- collapse groups
- choose whether a group contributes to the displayed total where product semantics allow it

Each account row:

- bank/provider icon or fallback account icon
- account display name
- optional masked account identifier
- current balance in account currency
- converted base-currency balance when currency differs
- pending indicator/value if available
- sync/error badge only when needed

Group header shows group total in base currency.

Do not show internal provider identifiers.

## 8.2 Net worth summary

Shows:

- current net worth
- absolute change for selected comparison period
- percentage change where mathematically meaningful
- small trend line
- assets total
- liabilities total

Net worth = included financial accounts + included manual assets + investment market value - included liabilities.

Transfers between included accounts do not change net worth.

## 8.3 Available until next income

Default calculation:

```text
current spendable balances
+ known income before next expected salary/pay-cycle income
- known upcoming fixed costs
- forecast variable spending until next income
```

Display:

- estimated available amount
- days until expected income
- short breakdown
- confidence/quality state: high / medium / limited data

This is an estimate. If salary/pay-cycle is not known, fall back to end-of-month and label it accordingly.

## 8.4 Income vs expenses

Default period: current month.

Default chart: grouped bars or two-value summary plus trend.

Rules:

- exclude linked transfers by default
- exclude transactions manually marked `exclude from statistics`
- refunds reduce the original expense category when linked
- show pending transactions separately or with lower emphasis; do not silently mix them into booked totals unless the user enables it

## 8.5 Budget focus

Shows up to four budgets sorted by urgency:

1. forecast over limit
2. over/near limit
3. highest usage percentage

Each row:

- budget name
- spent / available
- progress bar
- remaining amount
- end-of-period forecast

Click opens Budget detail.

## 8.6 Net worth / portfolio trend

Configurable data source:

- full net worth
- selected accounts/assets
- portfolio only
- selected asset classes
- selected securities

Supports actual history and optional forecast.

## 8.7 Upcoming contracts

Default horizon: next 30 days.

Shows:

- merchant/logo or category fallback icon
- contract name
- expected amount
- due date
- confidence/automatic/manual indicator only in detail, not as noisy primary text

## 8.8 Recent transactions

Default: latest 6 booked/pending transactions across all accounts.

Rows show:

- merchant logo if enabled and available; otherwise category icon
- merchant/counterparty
- category
- date/account secondary text
- amount
- pending/refund/transfer marker when applicable

## 8.9 Alerts & actions

Only show actionable items, for example:

- bank reauthorization required
- sync error
- budget forecast over limit
- contract due soon
- unmatched receipt
- transaction likely needing categorization

Do not show an empty permanent card. Empty state says `No actions needed`.

## 8.10 Purchase / receipt widget

Configurable variants:

- scan action + latest purchases
- spend by merchant
- spend by product category
- unmatched supermarket/drugstore/home-improvement transactions that may need a receipt

The detection is a prompt/suggestion, never an assertion that a receipt exists.

---

# 9. Transactions

## 9.1 List layout

Desktop uses a dense but readable table/list hybrid. Mobile uses transaction rows/cards.

Default grouping: by booking date.

Each transaction shows:

- merchant logo or category icon fallback
- merchant/counterparty
- category
- account
- date
- amount and original currency
- pending marker when applicable
- compact markers for split, refund, transfer, excluded-from-statistics, receipt linked

Merchant logos are user-configurable. Never make client-side third-party logo lookups containing private transaction text. Resolve normalized merchants through backend/local cached metadata. If unavailable, use category icon.

## 9.2 Filters

MVP filters:

- search text
- date range
- account/group
- category including descendants option
- income/expense
- booked/pending
- merchant/counterparty
- amount range
- currency
- transfer yes/no
- refund yes/no
- receipt linked/unlinked
- excluded from statistics yes/no

Desktop filter bar can expand advanced filters. Mobile opens a filter sheet.

## 9.3 Transaction detail interaction

Desktop: open a right-side detail drawer, approximately `420–520px` wide.

Mobile: open a full-screen detail view, not a narrow drawer.

Editable fields/actions:

- merchant/counterparty display normalization where allowed
- category
- split
- notes
- tags if implemented by backend
- exclude/include in statistics
- mark/correct transfer
- link refund/return
- link/unlink receipt/purchase
- link/unlink contract
- create categorization rule from correction

Saving a normal field should not force the user through a multi-step wizard.

## 9.4 Manual transactions

Manual accounts support manual transaction creation.

Required fields:

- account
- amount
- currency defaults to account currency
- date
- income/expense direction
- merchant/description
- category optional

Optional:

- note
- split
- receipt

Imported bank transactions are not silently converted into manual transactions.

## 9.5 Splits

A transaction may have multiple allocation lines.

Each line contains:

- amount
- category
- optional note/purchase link

Validation:

- allocation sum must equal transaction amount except when a documented reconciliation difference exists for purchase extraction
- UI always displays remaining unallocated amount while editing

Statistics use split lines instead of additionally counting the parent transaction.

## 9.6 Refunds / returns

A positive refund can link to:

- original transaction
- original split line
- purchase/order item

Partial refunds are supported.

Example: Amazon transaction contains three split items; one item is returned. Link the refund to that split/item so analytics reverse only that portion.

If a refund is linked, category analytics reduce the original expense rather than treating the refund as ordinary income.

## 9.7 Transfers

Transfer detection creates an explicit relationship between two transaction legs.

Rules:

- both legs remain real transactions on their accounts
- linked transfer is excluded from normal income/expense statistics by default
- user can confirm, reject or manually create/remove the transfer link
- a transfer may have an optional **purpose** such as `Savings`, `Vacation goal`, etc.; purpose is not an expense category
- changing transfer purpose must not make the transfer count as spending
- savings analytics may count transfer purpose separately

Do not solve transfers by assigning a normal category and hoping analytics infer the meaning.

`Exclude from statistics` remains an independent per-transaction flag for non-transfer cases.

---

# 10. Categories

## 10.1 Creation and onboarding

Do not ask the user to configure a large category taxonomy during signup.

At FullWorth Space creation/onboarding:

1. determine selected app language
2. copy the default category template for that language into the FullWorth Space as normal editable category records
3. continue onboarding

The seeded categories are not immutable system records.

## 10.2 Hierarchy

Support unlimited logical nesting depth in the data model.

UI behavior:

- tree view on desktop
- drill-in/tree list on mobile
- expand/collapse
- drag-and-drop where practical on desktop
- explicit `Move` action always available as accessible fallback
- multi-select
- bulk move
- bulk archive

Each category:

- name
- icon key
- color token/custom color where supported
- parent category
- archived state

## 10.3 Editing defaults

Users may edit every seeded/default category exactly like a user-created category:

- rename
- recolor
- change icon
- move
- add children
- archive
- merge/reassign

## 10.4 Delete/archive behavior

To protect history:

- unused category: may be permanently deleted after confirmation
- category used by transactions/rules/budgets: default action is archive
- user may reassign existing references to another category and then delete
- archived category remains visible on historical transactions and analytics but is hidden from normal new-category pickers unless `Show archived` is enabled

Never silently move historical transactions to `Other`.

## 10.5 Category picker

Picker requirements:

- search
- recent categories
- favorites optional later
- show hierarchy breadcrumb
- category icon/color
- quick `Create category` without leaving the transaction flow

---

# 11. Categorization rules

MVP rule builder is powerful enough for normal use but not a programming language.

A rule contains:

- enabled
- priority
- match mode: `all conditions` or `any condition`
- conditions
- actions
- scope: current account or FullWorth Space

Supported initial conditions:

- merchant/counterparty contains/equals
- transaction text contains
- account equals
- amount equals/range
- income/expense
- currency

Supported actions:

- set category
- set merchant normalization when supported
- mark excluded from statistics
- mark as transfer candidate only when sufficient transfer logic exists

Before saving, `Preview` shows matching transaction count and example rows.

Manual user classification always wins over automatic reclassification unless the user explicitly confirms a bulk overwrite.

---

# 12. Budgets

Use one universal budget model rather than separate incompatible budgeting systems.

## 12.1 Budget definition

A budget has:

- name
- amount and currency
- cycle: calendar month / salary cycle / custom
- category selection, optionally including descendants
- account selection: all or selected
- carry-over: on/off
- start date
- optional end date
- alert thresholds

Default carry-over is **off**. User can enable it per budget.

A budget may include multiple categories. Overlapping budgets are allowed because users may intentionally create both broad and focused views; the UI warns that the same spending can contribute to more than one budget.

## 12.2 Budget screen

Top summary:

- total budgeted
- spent
- remaining
- forecast end-of-period

Budget cards/list:

- progress
- spent/limit
- remaining
- period dates
- forecast
- status: on track / near limit / forecast over / over

Detail page includes:

- burn/trend chart
- included categories/accounts
- contributing transactions
- carry-over history
- period history

## 12.3 Salary cycle

If salary-cycle mode is selected, user chooses the detected/selected recurring income source and cycle anchor. If no reliable salary is detected, require explicit date/cycle configuration rather than guessing silently.

---

# 13. Contracts

Contracts come from two sources:

- automatically detected recurring-payment candidate, confirmed by user
- manually created

List supports:

- active/archived
- contract type
- merchant/provider
- expected amount
- cadence
- next due date

Detail supports:

- merchant logo/fallback icon
- automatic/manual value mode
- linked transactions
- payment trend
- next expected payment
- start/end dates
- cancellation/notice metadata when available
- notes

Loans/credit liabilities use the existing first-class contract/liability model and show amortization/payoff information when data is complete.

---

# 14. Net worth, assets and investments

## 14.1 Net worth page

Header:

- current net worth
- assets
- liabilities
- change

Sections:

- history/forecast chart
- accounts by group
- investments
- manual assets
- liabilities

Users can include/exclude individual manual assets from net-worth totals without deleting them.

## 14.2 Manual assets

Support at least:

- property
- vehicle
- cash/other
- precious metals/collectibles as generic assets

User enters current value and may add historical value snapshots.

## 14.3 Investment presentation

When investment/security data exists, support filtering by:

- all portfolio
- ETF
- stock
- crypto
- other security type
- selected individual securities

Portfolio charts can show:

- market value
- contribution/cost basis
- absolute gain/loss
- percentage performance

## 14.4 Performance math

Never calculate investment percentage as `(current value - some stale total deposits) / deposits` when buys/sells/cash flows have occurred.

Expose distinct concepts:

- unrealized gain/loss
- realized gain/loss
- distributions/dividends where available
- total investment result
- Time-Weighted Return (TWR) for investment performance independent of cash-flow timing
- Money-Weighted Return / XIRR for the user's personal cash-flow-weighted return when sufficient dated cash flows exist

UI default percentage for a portfolio performance chart: **TWR**, clearly labeled `Performance`.

Where TWR cannot be calculated reliably because historical valuation/cash-flow data is incomplete, show `Not enough history` instead of a misleading percentage.

A buy/sell/deposit/withdrawal must not create a fake performance jump.

Security detail may additionally show position cost basis and unrealized percentage for currently held units.

---

# 15. Analytics

Analytics is a first-class section, not a collection of hard-coded charts.

## 15.1 Analytics home

Default reports:

- Income vs expenses
- Expenses by category
- Category trend
- Merchant spending
- Account cash flow
- Net worth history
- Budget history
- Savings / transfer-purpose trend
- Investment performance when available

## 15.2 Chart builder

`Create analysis` uses a guided configuration:

1. What: measure/data source
2. Scope: accounts/categories/budgets/assets/securities/contracts
3. Period and granularity
4. Visualization
5. Comparison
6. Forecast if supported
7. Save name

Examples supported by the same system:

- all expenses by month
- only groceries by week
- selected accounts income vs expense by quarter
- ETF-only portfolio market value for five years
- Apple + NVIDIA + selected ETF as a custom investment view
- selected budgets over the last year

Saved analyses can be added as Dashboard widgets without rebuilding their configuration.

---

# 16. Purchases and receipts

## 16.1 Purchases home

Primary action: `Scan receipt`.

Default sections:

- receipts needing review
- unmatched likely receipt transactions
- recent purchases
- spend by merchant/product category summary

Likely receipt prompts may target merchant classes such as supermarket, drugstore or home-improvement stores. They are suggestions and can be dismissed.

## 16.2 Scan/review flow

1. capture/upload receipt
2. extraction
3. review screen with receipt image and extracted data side-by-side on desktop, stacked on mobile
4. edit merchant/date/total/items/categories
5. choose/link matching bank transaction
6. reconcile difference
7. confirm

Always show:

- bank transaction amount
- receipt total
- item total
- unallocated difference

User must be able to confirm a legitimate difference explicitly; never hide it.

## 16.3 Purchase analytics

Support grouping by:

- merchant/store
- purchase category
- line-item/product category
- time

A full pre-shopping checklist/planner is deferred.

---

# 17. Accounts

Accounts page is separate from the Dashboard widget.

List/group view:

- same account groups as Dashboard
- connection status
- balance/original currency
- base-currency equivalent
- last successful sync
- actionable reconnect state

Account detail:

- balance history when available
- transactions
- account metadata
- sync state
- owners/sharing data may remain hidden/minimal in single-space MVP unless required for current security administration
- manual account editing/transaction creation where applicable

Bank cooldown information should be shown only when useful, not as developer terminology. Example: `Next manual refresh available at 14:30`.

---

# 18. Multi-currency rules

EUR is default base currency, but every amount retains original currency.

Display rules:

- account rows show native currency first
- if native != base, show smaller converted base value
- cross-account totals use base currency
- analytics use historical conversion rate appropriate to the transaction/value date where data exists
- transaction detail always preserves original amount/currency
- never overwrite original money amount with converted amount

If conversion rate is missing:

- show original value
- mark converted aggregate as incomplete
- do not silently assume `1:1`

Currency formatting follows selected locale.

---

# 19. Search

Global search opens from the app header.

Search at least:

- transactions
- merchants/counterparties
- accounts
- categories
- contracts
- purchases/receipts
- assets

Results grouped by entity type.

Keyboard desktop shortcut: `/` focuses/opens search unless focus is already inside an editable field.

Do not search raw provider payloads or secrets.

---

# 20. Notifications

MVP preference UI covers currently supported notification types:

- bank reauthorization required
- bank sync error
- contract due soon
- budget near limit
- budget forecast over limit
- backup failed where user-facing

The UI may reserve sections for future channels, but only Push is active initially.

Do not create fake disabled e-mail controls that imply the feature works. Label future channels clearly if shown at all.

---

# 21. Settings

Settings sections:

- Profile
- Language & region
- Appearance
- Privacy
- Dashboard & navigation
- Notifications
- Accounts & bank connections
- Categories & rules shortcut
- Security / sessions / passkeys
- Export
- Backups/admin where authorized
- API access where authorized

Dashboard & navigation settings contain:

- Shared vs separate desktop/mobile dashboard layouts
- mobile bottom-nav slot configuration
- merchant logos on/off
- reset Dashboard

Language launch options: German and English.

Changing language updates the UI immediately and updates `<html lang>`.

Changing language does **not** rename categories the user already owns. Seeded categories are normal user data after creation.

---

# 22. FullWorth Space future-proofing

MVP UI has one implicit current FullWorth Space.

Implementation requirements:

- central `currentFullWorthSpaceId`/context abstraction
- every finance API request is scoped through the established authenticated context
- dashboard/preferences should be representable per user and, where data semantics require, per FullWorth Space
- no component may hard-code a globally unique default-space ID
- do not expose a visible switcher until multi-space UX is implemented

When the future switcher is added, pages/widgets should only need their context source changed, not their data model rewritten.

---

# 23. Gamification future-proofing

Game Mode is deferred and optional.

Base MVP must not persist finance truth as XP, levels or streaks. Future gamification consumes existing domain events/metrics such as:

- budget status
- savings progress
- categorization completion
- goals

Requirements now:

- keep financial calculation services independent of visual mode
- use semantic design tokens
- keep Dashboard widgets reusable
- do not place game-specific fields in core transaction/category/budget entities unless a future accepted product decision requires them

A future user must be able to switch Game Mode off and get the normal finance app with identical underlying data.

---

# 24. Loading, empty, error and offline states

Every page/widget must implement all applicable states.

Loading:

- skeleton for cards/lists where layout is known
- spinner only for small localized actions

Empty:

- explain why empty
- provide one relevant primary action
- no large decorative illustration required

Error:

- human-readable message
- retry where safe
- never expose stack traces/provider secrets

Offline:

- clearly state that current finance data is unavailable offline unless already explicitly supported
- app shell may load
- do not present stale sensitive data as current

---

# 25. Accessibility requirements

Treat `docs/ACCESSIBILITY.md` as required, not optional polish.

Minimum implementation rules:

- full keyboard navigation
- visible `:focus-visible`
- semantic landmarks/headings
- `aria-current` for active navigation
- labels for all controls
- accessible names for icon-only buttons
- native dialog behavior or equivalent focus management
- skip-to-content on desktop/web
- `<html lang>` follows selected locale
- reduced-motion preference respected
- text/color contrast verified in Light and Dark
- chart has a text summary/data alternative; chart meaning cannot depend on color alone

Drag-and-drop always has an accessible non-drag alternative such as Move up/down or explicit Move action.

---

# 26. Frontend component contract

The active cleanup/migration plan for this frontend is documented in `docs/FRONTEND_ARCHITECTURE_CLEANUP_PLAN.md`. New frontend work must not introduce additional workaround layers that conflict with that plan.

Do not create a giant design-system framework. Create small reusable UI modules/components.

Required shared concepts:

```text
AppShell
SidebarNav
BottomNav
PageHeader
GlobalSearch
PrivacyToggle
MoneyValue
SensitiveValue
AccountIcon
MerchantLogo
CategoryIcon
StatusBadge
FilterBar
PeriodPicker
ScopePicker
Chart
ChartLegend
WidgetCard
DashboardGrid
DashboardEditBar
TransactionRow
TransactionDetail
CategoryTree
CategoryPicker
BudgetProgress
EmptyState
ErrorState
LoadingSkeleton
ConfirmDialog
BottomSheet / MobileFullScreenPanel
Toast
```

Current FullWorth.Web is a server-hosted web app/PWA with static browser assets. Do not introduce a new fourth application project solely for UI work. If JavaScript is split, prefer small feature-focused ES modules within `FullWorth.Web/wwwroot` rather than growing one monolithic file.

Recommended browser structure as the UI grows:

```text
wwwroot/
  app.js
  app.css
  ui/
    shell.js
    privacy.js
    money.js
    dialogs.js
    charts.js
    widgets.js
  features/
    dashboard.js
    transactions.js
    accounts.js
    budgets.js
    contracts.js
    networth.js
    analytics.js
    purchases.js
    categories.js
    rules.js
    settings.js
  locales/
```

This is organization guidance, not permission to duplicate backend business logic in JavaScript.

---

# 27. Data and calculation rules visible in UI

These rules are non-negotiable because otherwise analytics become misleading.

1. Transfers linked between included accounts are excluded from normal income/expense statistics by default.
2. Transfer purpose/savings classification is separate from expense category.
3. Split transaction allocations replace the parent for category/statistical allocation; never double count.
4. Linked refunds reverse the relevant original expense/split/item rather than appearing as ordinary income.
5. `Exclude from statistics` is explicit and independently user-correctable.
6. Pending and booked status must remain distinguishable.
7. Investment cash flows must not create fake percentage performance.
8. Cross-currency aggregates use conversion data and identify incomplete conversion.
9. Forecast values are visually and semantically distinct from actual values.
10. Manual classification is protected from silent rule/AI overwrite.

---

# 28. MVP acceptance flows

The UI is not complete until these end-to-end flows work.

## Flow A — first useful dashboard

1. user logs in
2. Overview loads without configuration
3. account groups and balances are visible
4. income/expense, budgets, contracts and recent transactions show useful states
5. user can enable Privacy without navigating away

## Flow B — dashboard customization

1. enter Edit dashboard
2. move/resize widget on desktop
3. configure account/category/period scope
4. add a chart widget
5. save automatically or via Done
6. reload: layout/configuration remains
7. mobile renders shared list correctly
8. switch to separate layouts and reorder mobile independently

## Flow C — categorize and split

1. open transaction
2. change category
3. optionally create a rule
4. split transaction into multiple categories
5. analytics and budget totals use split values once, not parent + split

## Flow D — transfer

1. two matching account transactions are detected as transfer
2. user confirms
3. income/expense totals no longer count either leg
4. user sets purpose `Savings`
5. savings view may count it, expense view still does not
6. user can undo transfer relationship

## Flow E — refund

1. original purchase has multiple lines/splits
2. refund transaction arrives
3. user links refund to one item/split
4. corresponding category expense is reduced only by refunded amount

## Flow F — receipt

1. scan/upload
2. extraction appears
3. user corrects items
4. link transaction
5. difference is visible
6. confirm
7. purchase analytics and category budgets use confirmed item allocations without double counting

## Flow G — investment performance

1. portfolio has a value history and dated buys/sells/cash flows
2. TWR performance displays
3. adding a deposit/buy does not create a fake positive return jump
4. realized/unrealized values remain separately understandable

## Flow H — multi-currency

1. EUR base space has non-EUR account
2. account shows native and converted values
3. total uses base currency
4. transaction detail preserves original currency
5. missing rate produces explicit incomplete state, not 1:1 conversion

---

# 29. Implementation order for UI work

Implement in this order so each step can be tested:

1. App shell, navigation, responsive breakpoints, privacy primitives
2. Shared formatting/components: Money, icons, statuses, loading/error/empty states
3. Dashboard grid/list persistence and default dashboard
4. Account overview and Accounts screens
5. Transactions list/detail/manual/split/transfer/refund UI
6. Category tree/picker and Rules UI
7. Budgets and budget widget
8. Contracts and upcoming-contract widget
9. Net worth/assets/investment views and performance labels
10. Analytics chart builder and saved analyses/widgets
11. Purchases/receipt scan/review/reconciliation UI
12. Search, notifications and remaining Settings
13. Accessibility verification, responsive polish, keyboard/screen-reader pass
14. End-to-end acceptance flows above

Do not start the marketing landing page or Game Mode before this app flow is stable enough for product testing.

---

# 30. Definition of done for each screen

A screen is done only when:

- normal path works with real API data
- authorization scope is respected
- desktop and mobile behavior matches this spec
- loading/empty/error states exist
- Privacy mode is verified
- DE and EN text exists
- Light and Dark work
- keyboard navigation works
- important destructive/bulk actions confirm appropriately
- values use shared money/currency formatting
- no finance calculation is duplicated ad hoc in the browser if Backend owns the calculation
- focused tests cover the screen's important contracts

If a developer encounters an unspecified cosmetic detail, choose the simplest option consistent with the tokens above. If a developer encounters an unspecified **financial behavior**, do not guess; add a product decision before shipping it.
