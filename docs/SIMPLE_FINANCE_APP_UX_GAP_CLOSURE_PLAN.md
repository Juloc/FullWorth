# FullWorth UX gap-closure plan

Status: **shipped — residual tracker**  
Scope: close every remaining gap found in the 2026-09-06 audit of the simple finance-app UX rework.  
Goal: make the rework functionally complete, not only visually similar.

> **Shipped.** Waves 1–4 (P0/P1/P2) are delivered and locked in by
> `tests/FullWorth.Web.Tests/FinanceUxGapClosureBaselineTests.cs`. This document is now a
> tracker: each item below is marked **Done**, and the few genuine residuals are collected
> under [Open residuals](#open-residuals). Do not treat the per-item "Implementation" blocks
> as pending work unless they are called out as a residual.

## Priority order

### P0 — correctness blockers

These are user-visible correctness issues and must be fixed before calling the rework done.

#### 1. Group -> bookings must work end to end — ✅ Done

Delivered: `TransactionQuery.AccountGroupId` resolves the group inside the current Space
(`TransactionsModule.cs` ~line 115), scoped to the user's visible accounts.

Original issue (resolved):
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

#### 2. Category descendant filtering must really work — ✅ Done

Delivered: `TransactionQuery.IncludeDescendants` resolves the selected subtree server-side
(`TransactionsModule.cs` ~line 129) across direct category and allocations.

Original issue (resolved):
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

#### 3. Analytics period must be consistent across every card — ✅ Done (one residual)

Delivered: category and merchant analytics take arbitrary `From`/`To`/`Granularity`
(`CategoryAnalyticsService.cs`, `MerchantAnalyticsService.cs`, `AnalyticsModule.cs`) and the
frontend drives every card off one `granularity` scope.

Original issue (resolved):
- UI sends `from/to/granularity`
- category and merchant analytics backend still use `year/month`

Implementation (shipped):
- shared analytics scope on the wire:
  - From
  - To
  - Granularity = week | month | quarter | year
  - AccountId?
  - AccountGroupId?
  - CategoryId?
  - IncludeCategoryDescendants
  - Merchant?
  - Currency
- migrate category analytics to arbitrary date ranges — done
- migrate merchant analytics to arbitrary date ranges — done
- previous-period comparison uses a preceding window of identical length — done
- trailing averages are defined per granularity (subtree-rolled) — done
- same scope reused in overview/chart/category/merchant cards — done

Residual — **selectable comparison modes are not implemented.** The comparison is always the
immediately preceding equal-length window; there is no `ComparisonMode` field (e.g. same-period-
last-year). Either build a selectable comparison mode or keep "preceding equal-length" as the
single supported mode. Tracked under [Open residuals](#open-residuals).

Files:
- `Modules/Analytics/AnalyticsModule.cs`
- `Modules/Analytics/Categories/CategoryAnalyticsService.cs`
- `Modules/Analytics/Merchants/MerchantAnalyticsService.cs`
- `features/analytics.js`

Acceptance:
- switching Week -> Month -> Quarter -> Year changes every applicable card
- category and merchant totals reconcile with transaction drill-down for the same scope
- previous-window trend uses exactly the previous equivalent period

#### 4. Contract normalized costs must come from the backend — ✅ Done

Delivered: `ContractView` exposes `MonthlyEquivalent` and `AnnualizedAmount`
(`ContractsModule.cs` ~line 55); the frontend consumes them and no longer does cadence math.

Original issue (resolved):
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

#### 5. Complete transaction filter sheet — ✅ Done

Delivered: URL-backed filter sheet in `features/transactions.js` and matching
`TransactionQuery` fields (`AccountGroupId`, `Merchant`, `MinAmount`, `MaxAmount`, `RefundOnly`,
`HasReceipt`, `Status`, `IgnoredOnly`, `IncludeDescendants`, `CategoryId`).

Scope (all shipped):
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

#### 6. Complete contract filters — ✅ Done

Delivered in `features/contracts.js`: `filterContracts()` filters by account, category, billing
cycle, type (`view.kind`) and lifecycle/status; the sort segment keeps all sorts below; and
grouping by account/category/type is implemented (`groupKeyFor`/`groupBucket`).

Filters (shipped):
- account
- category
- billing cycle
- type
- lifecycle/status

Sorts (shipped):
- next due
- monthly equivalent
- annualized amount
- account
- category
- name

Grouping (shipped):
- account
- type
- category

Note: grouping is coupled to the active sort dimension (grouping switches on when the list is
sorted by account/category/type) rather than being an independent grouping selector. An
independent grouping control is optional and left as a minor residual — see
[Open residuals](#open-residuals).

Implementation rule:
- small lists may remain client-filtered initially
- the filter state must be URL-restorable if the contracts page becomes deep-linkable
- server-side filters become mandatory before pagination is added

Acceptance:
- user can reproduce the practical filter dimensions shown in the reference UX

#### 7. Consistent identity: brand -> category -> generic — ✅ Done

Delivered: contracts now pass `categoryIconKey` into the shared `identityIcon` component
(`features/contracts.js`), so bookings and contracts share brand -> category -> generic
fallback.

Original partial state (resolved):
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

#### 8. Add a configurable emergency-fund / Notgroschen target — ✅ Done

Delivered: the target is stored under the `wealth.emergencyFund` user preference and surfaced as
`EmergencyFundView` in `WealthModule.cs`; the frontend builds the card and edit dialog
(`buildEmergencyCard`/`openEmergencyFundDialog` in `features/networth.js`).

Original issue (resolved):
- wealth UI explicitly skips it because no target model/API exists

Implementation (shipped):
- emergency-fund target settings, persisted as a **per-user, per-space** `UserPreference`
  (keyed by `FinanceUserId` + `FullWorthSpaceId` + `Key`, `PreferencesModule.cs`):
  - enabled
  - target amount
  - optional source-account/group scope (`accountId` / `accountGroupId`)
  - optional automatic recommendation mode later
- expose target + current liquid amount to wealth overview — done
- show card only when configured — done
- allow edit from the card/details — done
- do not invent a default target silently — done

⚠ Needs decision: the plan said **per-space**, but the shipped storage is **per-user-per-space**
(each member has their own target inside a Space; it is not shared across the Space). Product call
required: is a personal target the intended behaviour, or should the emergency-fund target be a
single shared Space-level setting? The doc wording has been corrected to match the code; the
shared-vs-personal semantics still need a human decision.

Acceptance:
- configured target shows current / target and percentage
- disabling target removes the card
- value uses only explicitly defined liquid scope

---

## P2 — polish and verification

#### 9. Add an explicit "Alle Buchungen" entry — ✅ Done

Delivered: an explicit all-bookings row (`transactions.allTx`) opens `/transactions` without
account/group scope (`app.js`).

The product model expects a simple all-bookings entry in Overview/More.

Implementation:
- add a visible "Alle Buchungen" row in More and/or Overview
- opens `/transactions` without account/group scope

Acceptance:
- all bookings are reachable without first opening an account

#### 10. Analytics drill-down parity — ✅ Done

Delivered: category, merchant, period/bar and income/expense segments are keyboard-drillable
(`bindPeriodDrills`, `data-period-index`, `data-direction`, `analyticsTxScope` in
`features/analytics.js`) and carry from/to/category+descendants/merchant/account scope into the
transaction list.

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

#### 11. Accessibility and responsive verification — ✅ Done

Baseline locked by `FinanceUxGapClosureBaselineTests`: 44 px touch targets (`min-height:44px`),
focus-visible states (`.an-period-hit:focus-visible`), `role="button"`/`tabindex="0"` on
drillable segments, precached UX modules in `sw.js`. Manual DE/EN, dark/light, privacy-mode and
width sweeps performed during the rework.

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

#### 12. Performance verification — ◻ Partially done (residuals)

Delivered in `tests/FullWorth.Backend.Tests/Performance/TransactionQueryPerformanceTests.cs`:
- transaction group filter benchmark — done (combined scoped filter over 4,000 tx, mandatory in CI)
- descendant category benchmark — done (same combined test)
- combined filter benchmark — done (group + descendant + merchant + amount + status, <2000ms)
- 100k transaction load run — done (opt-in via `FULLWORTH_PERF=1`)
- smaller mandatory regression dataset in normal CI — done (the 4,000-row scoped test)

Still open (see [Open residuals](#open-residuals)):
- **analytics category/merchant arbitrary-range benchmark** — no perf/N+1 coverage exists for the
  category or merchant analytics services
- **representative ~10k dataset run** — coverage jumps from 4,000 (mandatory) to 100k (opt-in);
  there is no mid-size 10k representative run

CI:
- keep 100k suite opt-in if runtime is too high — done
- add a smaller mandatory regression dataset to normal CI — done

Acceptance:
- normal transaction list remains responsive with realistic large datasets — verified for tx queries
- analytics does not issue N+1 queries per category/merchant — **not yet measured** (residual)

---

## Open residuals

Waves 1–4 are shipped. These are the only genuine gaps still open — this is the live backlog:

1. **Analytics arbitrary-range performance / N+1 benchmark.** There is no perf test asserting the
   category and merchant analytics services stay responsive and issue no N+1 queries over an
   arbitrary date range. (Item 12.)
2. **Representative ~10k transaction dataset run.** Perf coverage jumps from the 4,000-row
   mandatory scoped-filter test to the 100k opt-in load harness; a mid-size ~10k run is missing.
   (Item 12.)
3. **Selectable analytics comparison modes.** Only the immediately preceding equal-length window
   is supported; no `ComparisonMode` (e.g. same-period-last-year). Build it or accept the single
   mode and drop the wording. (Item 3.)
4. **Independent contract grouping selector (optional).** Grouping works today but is tied to the
   active sort dimension (account/category/type); a standalone grouping control is not
   implemented. Low priority. (Item 6.)

Needs a human decision (do not resolve silently):
- **Emergency-fund target scope.** Stored per-user-per-space, but the plan said per-space. Decide
  whether personal targets are intended or the setting should become a shared Space-level value.
  (Item 8.)

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

### Wave 1 — correctness — ✅ shipped
1. Transaction group scope
2. Category descendants
3. Analytics arbitrary ranges
4. Contract normalized DTOs

Gate met: no known wrong totals or fake-working filters.

### Wave 2 — missing filters and identity — ✅ shipped
5. Full transaction filter backend + sheet
6. Full contract filters
7. Shared identity completion

Gate met: all agreed filters work end to end.

### Wave 3 — wealth and navigation completeness — ✅ shipped
8. Emergency fund target
9. Explicit All bookings entry
10. Analytics drill-down parity

Gate met: all originally agreed user flows are reachable.

### Wave 4 — hardening — ✅ shipped (perf residuals tracked)
11. accessibility/responsive test pass
12. performance regression coverage
13. DE/EN + light/dark + privacy verification

Gate met (release candidate): the two analytics/mid-size perf items remain as tracked residuals,
see [Open residuals](#open-residuals).

---

## Definition of done

This gap-closure work is complete when the criteria below are met. All functional criteria are
met; the only remaining gaps are the perf residuals under [Open residuals](#open-residuals).

- ✅ group booking drill-down works server-side
- ✅ category descendants work server-side
- ✅ every analysis card obeys Week/Month/Quarter/Year
- ✅ contract monthly/annual numbers come from the backend and are correct
- ✅ transaction filter sheet contains all agreed filters and they are server-backed
- ✅ contract filters include account/category/cycle/type/status
- ✅ identity fallback is consistent across bookings and contracts
- ✅ emergency fund target is configurable and visible when enabled (scope semantics: see item 8 decision)
- ✅ all bookings have a direct entry
- ✅ analytics drill-down reconciles to the transaction list
- ✅ accessibility/responsive/privacy verification passes
- ✅ mandatory regression tests cover the new behavior
- ◻ large-data performance has been measured — transaction queries yes; analytics arbitrary-range /
  N+1 and a ~10k mid-size run are the open residuals
