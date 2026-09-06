# FullWorth UX gap-closure plan

Status: **implementation plan**  
Scope: close every remaining gap found in the 2026-09-06 audit of the simple finance-app UX rework.  
Goal: make the rework functionally complete, not only visually similar.

## Priority order

### P0 — correctness blockers

These are user-visible correctness issues and must be fixed before calling the rework done.

#### 1. Group -> bookings must work end to end

Current issue:
- frontend sends `accountGroupId`
- backend transaction endpoint/query does not support it

Implementation:
- extend `TransactionQuery` with `AccountGroupId`
- expose `accountGroupId` on `GET /api/transactions`
- resolve the group server-side inside the current FullWorth Space
- only include accounts visible to the current user
- group count/pagination/search/filter must run inside the scoped query
- invalid/inaccessible group must return no cross-space data

Files:
- `src/FullWorth.Backend/Modules/Transactions/TransactionsModule.cs`
- transaction authorization/integration tests

Acceptance:
- tapping a group shows exactly all bookings of its visible accounts
- tapping an account still shows only that account
- reload/back/forward preserve scope
- inaccessible group IDs cannot leak transactions

#### 2. Category descendant filtering must really work

Current issue:
- frontend sends `includeDescendants=true`
- transaction backend ignores it

Implementation:
- add `IncludeDescendants` to `TransactionQuery`
- resolve the selected category subtree server-side
- apply subtree matching to:
  - direct transaction category
  - transaction allocations
  - purchase-item allocations

Acceptance:
- tapping a parent category in analytics shows bookings from that category and every child category
- direct child filtering still works
- no duplicate transactions

#### 3. Analytics period must be consistent across every card

Current issue:
- UI sends `from/to/granularity`
- category and merchant analytics backend still use `year/month`

Implementation:
- introduce a shared analytics scope model:
  - From
  - To
  - Granularity = week | month | quarter | year
  - AccountId?
  - AccountGroupId?
  - CategoryId?
  - IncludeCategoryDescendants
  - Merchant?
  - ComparisonMode
  - Currency
- migrate category analytics to arbitrary date ranges
- migrate merchant analytics to arbitrary date ranges
- previous-period comparison must use a preceding window of identical length
- trailing averages must be defined clearly per granularity or omitted when meaningless
- reuse the same scope in overview/chart/category/merchant cards

Files:
- `Modules/Analytics/AnalyticsModule.cs`
- `Modules/Analytics/Categories/CategoryAnalyticsService.cs`
- `Modules/Analytics/Merchants/MerchantAnalyticsService.cs`
- `features/analytics.js`

Acceptance:
- switching Week -> Month -> Quarter -> Year changes every applicable card
- category and merchant totals reconcile with transaction drill-down for the same scope
- previous-window trend uses exactly the previous equivalent period

#### 4. Contract normalized costs must come from the backend

Current issue:
- frontend expects `monthlyEquivalent` and `annualizedAmount`
- list DTO does not expose them

Implementation:
- extend `ContractView` with:
  - MonthlyEquivalent
  - AnnualizedAmount
- compute both with the existing `ContractCycle.PeriodsPerYear` logic
- use one backend calculation path for list/detail/detection
- remove any duplicate client-side cadence math

Acceptance:
- contract summary monthly/annual totals are correct
- monthly and annual sort work
- grouped monthly sums work
- detail and list show the same normalized values

---

## P1 — missing UX features from the agreed scope

#### 5. Complete transaction filter sheet

Add:
- account
- account group
- merchant
- minimum amount
- maximum amount
- refund only
- receipt linked
- booked / pending / all
- excluded from statistics
- transfers
- category
- date range
- income / expense

Backend additions:
- AccountGroupId
- Merchant / MerchantId-equivalent resolver
- MinAmount
- MaxAmount
- RefundOnly
- HasReceipt
- Status

Rules:
- filters are URL-backed
- active filter count includes every active filter
- transaction count and pagination apply after all server-side filters

Acceptance:
- every visible filter changes the backend result, not only the current page in JS
- reloading preserves filters
- filters combine correctly

#### 6. Complete contract filters

Add filters for:
- account
- category
- billing cycle
- type
- lifecycle/status

Keep sorts:
- next due
- monthly equivalent
- annualized amount
- account
- category
- name

Optional grouping:
- account
- type
- category

Implementation rule:
- small lists may remain client-filtered initially
- the filter state must be URL-restorable if the contracts page becomes deep-linkable
- server-side filters become mandatory before pagination is added

Acceptance:
- user can reproduce the practical filter dimensions shown in the reference UX

#### 7. Consistent identity: brand -> category -> generic

Current partial state:
- transactions use brand catalog + category icon fallback
- contracts do not pass category identity into the shared component
- backend transaction DTO has category identity but no resolved merchant identity

Implementation:
- keep the cloud/custom brand catalog as source of brand assets
- add a reusable presentation DTO or resolver output:
  - merchant display name
  - resolved brand key
  - logo asset path
  - category icon key
- contracts must pass category icon fallback
- recent bookings, upcoming contracts, transaction list and contract list must use the same resolver
- no client-side third-party logo lookups

Acceptance:
- known merchant -> brand logo
- unknown merchant with category -> category icon
- unknown merchant without category -> generic monogram/icon
- same entity looks the same everywhere

#### 8. Add a configurable emergency-fund / Notgroschen target

Current issue:
- wealth UI explicitly skips it because no target model/API exists

Implementation:
- add per-space emergency-fund target settings:
  - enabled
  - target amount
  - optional source-account/group scope
  - optional automatic recommendation mode later
- expose target + current liquid amount to wealth overview
- show card only when configured
- allow edit from the card/details
- do not invent a default target silently

Acceptance:
- configured target shows current / target and percentage
- disabling target removes the card
- value uses only explicitly defined liquid scope

---

## P2 — polish and verification

#### 9. Add an explicit "Alle Buchungen" entry

The product model expects a simple all-bookings entry in Overview/More.

Implementation:
- add a visible "Alle Buchungen" row in More and/or Overview
- opens `/transactions` without account/group scope

Acceptance:
- all bookings are reachable without first opening an account

#### 10. Analytics drill-down parity

Every applicable analytics row/segment should open the matching transactions.

Add drill-down for:
- category
- merchant
- period/bar
- income/expense segment where useful

Scope carried into URL:
- from
- to
- categoryId + descendants
- merchant/query
- account/group

Acceptance:
- totals visible in analytics reconcile with the opened booking list

#### 11. Accessibility and responsive verification

Verify:
- 44 px touch targets
- keyboard account/group drill-down
- keyboard transaction rows
- filter/sort sheets
- focus return after dialogs
- screen-reader labels on charts
- dark/light
- DE/EN
- 320 px mobile width
- tablet
- wide desktop
- reduced motion
- privacy mode

#### 12. Performance verification

Current load test exists but is opt-in.

Add:
- transaction group filter benchmark
- descendant category benchmark
- combined filter benchmark
- analytics category/merchant arbitrary-range benchmark
- representative 10k / 100k transaction test runs

CI:
- keep 100k suite opt-in if runtime is too high
- add a smaller mandatory regression dataset to normal CI

Acceptance:
- normal transaction list remains responsive with realistic large datasets
- analytics does not issue N+1 queries per category/merchant

---

## Test matrix

### Transactions

- all transactions
- one account
- one group
- category direct
- category descendants
- account + category
- group + date
- group + merchant
- group + amount range
- pending
- refund
- receipt linked
- transfer
- excluded
- mixed filters
- inaccessible account/group/category

### Analytics

For each granularity:
- week
- month
- quarter
- year

Verify:
- spend development
- income/expenses
- categories
- merchants
- net worth
- comparison window
- transaction drill-down reconciliation

### Contracts

Verify:
- monthly equivalent
- annualized amount
- type filter
- account filter
- category filter
- billing cycle filter
- status filter
- every sort
- account/category grouping
- brand/category identity fallback

### Wealth

Verify:
- trend
- allocation
- liabilities
- emergency fund target enabled
- emergency fund target disabled
- custom target scope
- incomplete FX behavior

---

## Delivery sequence

### Wave 1 — correctness
1. Transaction group scope
2. Category descendants
3. Analytics arbitrary ranges
4. Contract normalized DTOs

Gate: no known wrong totals or fake-working filters.

### Wave 2 — missing filters and identity
5. Full transaction filter backend + sheet
6. Full contract filters
7. Shared identity completion

Gate: all agreed filters work end to end.

### Wave 3 — wealth and navigation completeness
8. Emergency fund target
9. Explicit All bookings entry
10. Analytics drill-down parity

Gate: all originally agreed user flows are reachable.

### Wave 4 — hardening
11. accessibility/responsive test pass
12. performance regression coverage
13. DE/EN + light/dark + privacy verification

Gate: release candidate.

---

## Definition of done

This gap-closure work is complete only when:

- group booking drill-down works server-side
- category descendants work server-side
- every analysis card obeys Week/Month/Quarter/Year
- contract monthly/annual numbers come from the backend and are correct
- transaction filter sheet contains all agreed filters and they are server-backed
- contract filters include account/category/cycle/type/status
- identity fallback is consistent across bookings and contracts
- emergency fund target is configurable and visible when enabled
- all bookings have a direct entry
- analytics drill-down reconciles to the transaction list
- accessibility/responsive/privacy verification passes
- mandatory regression tests cover the new behavior
- large-data performance has been measured
