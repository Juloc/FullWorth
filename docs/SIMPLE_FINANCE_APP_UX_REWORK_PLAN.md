# FullWorth simple finance-app UX rework plan

Status: **implementation plan**  
Reference: the Finanzguru screenshots and interaction notes supplied on 2026-09-05.  
Goal: make FullWorth feel like a simple consumer finance application first, while keeping FullWorth's own branding, data model and advanced features.

This is an interaction/information-architecture reference, not a request to copy Finanzguru branding, artwork, icons or exact visual assets.

## 1. Current gaps in FullWorth

The backend already contains most of the necessary finance domains. The main gap is how they are presented.

Current implementation findings:

- app.js renders account groups primarily as collapsible containers. Clicking a group only expands/collapses it; it does not open the bookings for all accounts in the group.
- Account rows expose management actions but do not make the account itself a primary drill-down into that account's bookings.
- transactions.js renders a desktop-first table. It does not show merchant/brand identity, category-icon fallback or date-grouped mobile rows.
- AnalyticsModule and analytics.js already support several periods, but the page is still report/builder oriented. Category and merchant panels are currently fixed to the current month instead of following the selected page period.
- Contracts currently expose only an archived toggle plus detect/add actions. There is no simple type/sort/filter model like subscription / insurance / loan / other, annual cost or next-due sorting.
- Net worth currently leads with metric boxes and management panels. The information is available, but the main view is not yet a simple card-based story of trend, allocation and goals/reserve.
- Bank connections and account-management controls are too close to the everyday account overview.

## 2. Target information architecture

### Mobile primary navigation

Use exactly five bottom destinations:

1. **Übersicht**
2. **Verträge**
3. **Analysen**
4. **Vermögen**
5. **Mehr**

Mehr contains:

- Einkäufe
- Budgets
- Alle Buchungen
- Konten verwalten
- Bankverbindungen
- Kategorien
- Regeln
- Benachrichtigungen
- Einstellungen
- advanced/admin-only features when authorized

Transactions stay easy to reach without consuming a permanent bottom-nav slot: every account/group and an Alle Buchungen row opens the transaction list with the correct scope.

### Desktop

Keep a sidebar, but mirror the same mental model:

- Übersicht
- Konten & Buchungen
- Verträge
- Analysen
- Vermögen
- Einkäufe
- Budgets

Put configuration/maintenance sections below a separator:

- Kategorien
- Regeln
- Benachrichtigungen
- Einstellungen

Bank connections belong under account management, not as an equally prominent everyday destination.

## 3. Overview and account drill-down

The account overview becomes the main navigation hub for day-to-day money.

### Overview structure

Recommended mobile order:

1. available / spendable summary
2. account groups
3. recent bookings
4. contracts due soon
5. budget status / alerts
6. optional purchase/receipt prompt

### Account groups

Each group card/header shows:

- group icon
- group name
- number of accounts
- group total in base currency
- optional expand/collapse affordance

Interaction rules:

- **tap group body/name** -> open all bookings from all accounts in that group
- **tap chevron** -> only expand/collapse accounts
- **tap account row** -> open bookings for that account only
- edit/reorder actions stay separate and must stop event propagation

Routes:

- /transactions = all accessible bookings
- /transactions?accountId={id} = one account
- /transactions?groupId={id} = every accessible account in the group
- preserve search/filter state in the URL where practical

The transaction page header must show its scope:

- Alle Buchungen
- DKB Girokonto
- Sparkonten

and provide a clear back path.

### Backend change

Extend TransactionQuery with AccountGroupId.

TransactionStore.SearchForUserAsync must resolve the group only inside the active FullWorth Space and only return transactions from accounts accessible by the current user. Never accept a raw list of account IDs from the browser as authorization.

Also support account/group scope in count/pagination so large groups do not need client-side filtering.

## 4. Transaction list redesign

### Mobile row

Use the consumer-finance row pattern:

- left: 40–44 px identity icon
- center: merchant/counterparty as primary text
- secondary: category, account only when useful
- right: amount
- markers: transfer/refund/pending/receipt only when relevant

Group bookings by booking date with sticky/lightweight date headers.

Do not show a desktop table on mobile.

### Desktop

Keep a dense list/table hybrid, but add the same identity column and visual hierarchy. Do not maintain two unrelated transaction models.

### Merchant/brand identity

Resolution order:

1. local/cached merchant brand icon when a normalized merchant is known
2. category icon
3. generic transaction icon

Special transaction types can override this:

- transfer -> transfer icon
- savings-purpose transfer -> savings icon
- refund -> refund marker while keeping merchant/category identity

No client-side third-party logo lookup using private transaction text.

### Required merchant model work

The current Merchant registry has names and aliases but no visual identity. Add optional local metadata:

- BrandKey
- LogoAssetPath or equivalent local asset reference
- optional AccentKey
- user override / clear override

The backend transaction DTO should expose resolved presentation metadata, for example:

- merchantId
- merchantDisplayName
- brandKey
- logoAssetPath
- categoryId
- categoryName
- categoryIconKey

Frontend should not independently repeat normalization logic.

A small curated local brand pack is sufficient initially. Unknown brands must degrade cleanly to category icons.

## 5. Transaction filters

Use a compact default view and a bottom sheet/drawer for advanced filters.

Always-visible:

- search
- current scope (all/group/account)
- quick filter button

Filter sheet:

- account/group
- date range
- category
- income / expense
- booked / pending
- merchant
- amount range
- transfer
- refund
- receipt linked
- excluded from statistics

The active-filter count is shown on the filter button.

## 6. Analyses redesign

The analysis home should be a set of understandable cards, not a chart-builder workspace.

### Global cycle selector

Primary selector:

- **Woche**
- **Monat**
- **Quartal**
- **Jahr**

Each cycle gets a sensible default window:

- week -> last 12 weeks
- month -> last 12 months
- quarter -> last 8 quarters
- year -> last 5 years

Add previous/next window navigation and a clear window label such as Letzte 12 Monate.

### Default analysis cards

1. spending development
2. income vs expenses
3. fixed costs / contracts
4. spend by category
5. spend by merchant
6. net-worth development
7. optional daily need / cash-flow card when enough data exists

Each card contains:

- one clear question/title
- one chart
- one or two key numbers
- trend/comparison
- Zur Analyse / detail action

### Detail analysis

Detail view can add:

- period/cycle selector
- account/group filter
- category filter
- merchant filter
- comparison to previous period / average
- related booking list below the chart

Tapping a chart segment/category/merchant must be able to open the matching bookings.

### Move advanced builder

Keep the current custom chart builder, but move it under:

Analysen -> Eigene Analyse / Erweitert

It must not be the first or largest element on the normal analysis screen.

### Backend analytics contract

Introduce one reusable scoped query model instead of separate UI-specific assumptions:

- From
- To
- Granularity: week | month | quarter | year
- AccountId?
- AccountGroupId?
- CategoryId?
- IncludeCategoryDescendants
- MerchantId?
- ComparisonMode
- base currency

Refactor overview/category/merchant reports to respect the selected period. The current behavior where category/merchant analysis always means the current calendar month must be removed.

## 7. Contracts redesign

### Main page

Top summary:

- average contract cost per month
- optional annualized total
- Analyse action

Then a compact contract list.

Contract row:

- merchant brand icon or category icon fallback
- contract/provider name
- type/category as secondary text
- amount
- recurrence (monatlich, jährlich, ...)
- next due date when useful

### Filter/sort sheet

Filters:

- type: subscription / contract / insurance / loan / other
- account
- category
- active / archived
- billing cycle

Sorts:

- next due
- monthly equivalent
- annual cost
- account
- category
- name

Optional group modes:

- by account
- by type
- by category

The current detect-price-change functionality stays, but becomes a contextual action/alert instead of dominating the list header.

### Backend

For small datasets filtering can begin client-side, but add query parameters once pagination is introduced. Expose monthlyEquivalent and annualizedAmount in list DTOs so every client uses the same math.

## 8. Net worth redesign

The first screen should explain wealth before showing management tools.

Recommended card order:

1. **Wie entwickelt sich dein Vermögen?**
   - current net worth
   - trend
   - selected time cycle
2. **Verteilung deines Vermögens**
   - accounts
   - investments
   - real estate
   - other assets
   - liabilities as a clearly separate negative component
3. **Reserve / Notgroschen**
   - only when the user has configured a target
4. optional portfolio performance
5. details / manage assets and liabilities

Asset, liability, loan and valuation editors move behind Details or contextual actions rather than occupying the first viewport.

## 9. Visual direction

Use the screenshots for information hierarchy, not for brand copying.

FullWorth rules:

- keep FullWorth's own logo and accent system
- neutral app background
- white/elevated cards in light mode
- restrained borders and shadows
- 16–20 px card radius
- one strong primary text color
- one accent for actions/selection
- semantic green/red only for financial meaning
- category colors only where they improve scanning
- large values with tabular numerals
- compact mobile top bar
- bottom navigation with selected-state pill/background
- 44 px minimum touch targets
- no decorative hero area in authenticated screens

Use the existing semantic theme tokens rather than hard-coded Finanzguru colors.

## 10. Component work

Create/reuse shared components instead of page-specific markup:

- AppTopBar
- BottomNav
- SectionCard
- MoneyValue
- IdentityIcon
- MerchantIdentity
- CategoryIcon
- AccountRow
- TransactionRow
- DateGroup
- PeriodCycleSelector
- FilterSheet
- SummaryMetric
- TrendValue
- AnalysisCard
- EmptyState

In the current vanilla module structure these can initially be shared JS render helpers + shared CSS classes; a framework rewrite is not required for this UX pass.

## 11. Concrete file-level implementation map

### Web

- src/FullWorth.Web/wwwroot/index.html
  - simplify mobile nav
  - remove desktop-first transaction table requirement from mobile structure
  - add scoped transaction header/filter sheet containers
  - simplify analysis and contract shells

- src/FullWorth.Web/wwwroot/app.js
  - account/group click-through routing
  - separate expand chevrons from drill-down actions
  - route/query-state handling

- src/FullWorth.Web/wwwroot/features/accounts-ux.js
  - preserve visual customization
  - make account/group identity reusable
  - prevent edit/reorder controls from triggering drill-down

- src/FullWorth.Web/wwwroot/features/transactions.js
  - date-grouped rows
  - account/group scope
  - identity resolver output
  - mobile filter sheet
  - keep detail drawer/full-screen detail behavior

- src/FullWorth.Web/wwwroot/features/analytics.js
  - card-based landing
  - week/month/quarter/year cycle
  - scoped filters
  - detail drill-down
  - move chart builder to advanced

- src/FullWorth.Web/wwwroot/features/contracts.js
  - summary
  - type/sort/filter sheet
  - merchant/category identity
  - grouping

- src/FullWorth.Web/wwwroot/features/networth.js
  - trend/composition/reserve cards first
  - management panels behind details

- src/FullWorth.Web/wwwroot/app.css
- src/FullWorth.Web/wwwroot/design-depth.css
- src/FullWorth.Web/wwwroot/features/accounts-ux.css
  - shared card/list/mobile primitives
  - bottom-nav selection
  - responsive transaction rows

### Backend

- Modules/Transactions/TransactionsModule.cs
  - AccountGroupId query
  - presentation identity fields
  - filter/pagination support

- Modules/Merchants/MerchantModule.cs
  - merchant visual metadata
  - alias -> merchant -> brand resolution

- Modules/Analytics/AnalyticsModule.cs
  - granularity/scoped analytics query
  - category and merchant reports obey selected period/scope

- Modules/Contracts/ContractsModule.cs
  - monthly equivalent / annualized amount in list output
  - optional server-side filters/sorts

- category module/catalog
  - stable IconKey + optional visual metadata for fallback identity

## 12. Delivery phases

### Phase A — navigation and drill-down

- new mobile bottom nav
- account/group -> scoped bookings
- all-bookings route
- URL-backed transaction scope
- responsive mobile transaction list
- date grouping

**Acceptance:** from Overview, one tap on a group shows exactly the bookings of its accounts; one tap on an account shows only that account.

### Phase B — transaction identity

- merchant brand metadata
- category icon metadata/fallback
- reusable identity component
- apply to bookings, contracts, recent-booking widgets and upcoming-contract widgets

**Acceptance:** every booking has a stable left-side identity without external client-side lookups.

### Phase C — simple analyses

- W/M/Q/Y selector
- analysis cards
- scoped filters
- all cards obey period/scope
- drill-down to bookings
- move custom builder to advanced

**Acceptance:** changing from month to quarter changes every applicable analysis consistently.

### Phase D — contracts and net worth

- contract summary + filters/sorts/grouping
- wealth trend/allocation/reserve card hierarchy
- move editing/management surfaces down one level

**Acceptance:** the first viewport answers what is happening; editing remains available without dominating the screen.

### Phase E — polish

- light/dark parity
- mobile/desktop spacing
- skeleton/loading states
- empty/error states
- keyboard/accessibility
- privacy-mode verification
- performance tests with thousands of bookings

## 13. Definition of done

The UX rework is done when:

- account and group drill-down works from Overview and Accounts
- transaction scopes are backend-authorized and URL-restorable
- mobile transactions are date-grouped rows, not a compressed table
- brand logo -> category icon -> generic icon fallback is consistent
- contract rows use the same identity system
- analyses support week/month/quarter/year and all cards honor the selection
- category/merchant analysis no longer silently ignores the selected period
- contracts have meaningful type/filter/sort options
- the net-worth first screen is trend/allocation focused
- primary mobile navigation contains only five items
- advanced configuration still exists but is one level deeper
- FullWorth branding remains distinct
- privacy mode, DE/EN, light/dark and accessibility still pass
