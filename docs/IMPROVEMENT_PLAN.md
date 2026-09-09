# Improvement plan

The one active plan for FullWorth. Everything here is a verified defect: each item was traced in the
code and then independently re-checked by a second pass that tried to refute it, and only what survived
is listed. Findings that did not survive verification are not here.

Priority follows impact on the user's money, then security, then everything else:

| | Meaning |
| --- | --- |
| **P0** | A wrong monetary figure reaches the user, or data is lost |
| **P1** | Wrong or unusable behaviour in a core path, or a real security weakness |
| **P2** | Correct but misleading, or a defect in a secondary path |
| **P3** | Cosmetic, or a nice-to-have consistency fix |

Status values: `OPEN`, `IN PROGRESS`, `DONE` (with the commit), `NEEDS DECISION` (waiting on the owner).

Audited 2026-09-09 against `main` at `3232803` / cloud `15d05ec` / deploy `87000ff`.

---

## P0 — wrong money or data loss

### P0-1 Dot-decimal amounts were imported 100x too large — `DONE` (1776f1c)

**Cause.** `ImportParityModule.ParseAmount` and `ImportMappingParityModule.ParseAmount` parsed with a
fixed `de-DE` culture and `NumberStyles.Number` (which includes `AllowThousands`) *before* trying the
invariant culture. .NET does not validate group sizes, so `"1234.56"` parsed successfully as `123456`
and the invariant branch was dead. For `.xlsx` this was guaranteed rather than locale-dependent, because
OOXML always stores cell values invariant.

**Impact.** A 0.99 coffee was committed as 99.00. Every affected account's balance history, budgets,
analytics and net worth were wrong by a factor of 100, with nothing to indicate it.

**Fix.** All four importers now share `Modules/Parity/ImportNumber.cs`, which decides from the
separators in the text, not from a culture, and rejects malformed input on the segments. The genuinely
ambiguous "single separator plus exactly three digits" case is a caller policy: statements read it as
grouping, prices as decimals. `FinanzguruWorkbookReader` had the same bug mirrored and is fixed too.

**Verified.** 249 import/investment/purchases tests, including 7 end-to-end cases through the real
upload and commit endpoints and 28 unit cases over the parser.

### P0-2 Net worth adds foreign-currency balances at face value — `OPEN`

**Cause.** `Modules/Portfolio/NetWorthSnapshotService.cs:218` buckets each account's latest
`BalanceSnapshot.Amount` by the **account's** `Currency` and never reads the snapshot's own `Currency`.
The same mistake sits in `Modules/Analytics/AnalyticsModule.cs:225` and `:240` for the dashboard.

**Impact.** A balance reported in a currency other than the account's declared currency is summed 1:1
into the wrong bucket and then FX-converted with the wrong rate. `WealthModule` does it correctly off
the snapshot's own currency, so the same data produces **two different net-worth numbers** depending on
which surface you look at.

**Target.** One shared projection that reads `(Amount, Currency)` as a pair from the balance row and
converts before bucketing. The account's declared currency is metadata, never a unit of measure for a
balance row.

**Acceptance.** An account declared EUR with a USD balance row appears in the USD bucket and in the
converted total exactly once, at the USD rate. Dashboard, net-worth history and Wealth report the same
figure for the same data.

**Tests.** Seed an account whose declared currency differs from its balance row currency; assert the
dashboard total, `/api/net-worth/history` and the Wealth total agree and use the balance currency's
rate. No test does this today — that is why the divergence is invisible in CI.

### P0-3 The headline net worth ignores every loan and every portfolio — `OPEN`

**Cause.** `Modules/Analytics/AnalyticsModule.cs:301` computes the Overview net-worth and "available"
tiles from a rule that never reads `db.Loans` and never reads investment portfolios.

**Impact.** The number the user sees first is **overstated by every mortgage and loan** and understated
by every portfolio. It disagrees with the Wealth page, which does read them.

**Target.** The Overview tiles must consume the same aggregate as the Wealth page rather than a second
private rule. One net-worth definition, one implementation.

**Acceptance.** With one loan and one portfolio present, Overview and Wealth report the same net worth;
a loan reduces it, a portfolio increases it.

**Tests.** Integration test with an account, a loan and a portfolio asserting equality across the two
surfaces.

### P0-4 An imported account cannot show its own value — `DONE` (see below)

**Cause.** Three things compound. `Modules/Import/FinanzguruImportModule.cs:269` never writes a
`BalanceSnapshot` and creates the account with `IsActive=false` and `IncludeInNetWorth=false`. No API
can then give that account a balance — the one that could refuses on a `Provider == "manual"` gate. And
`Modules/Import/FinanzguruAccountReconciliationService.cs:211` re-applies both flags on *every* bank
sync in the space and on every re-import, wiping a user override.

**Impact.** This was the reported symptom. An imported account rendered flat or empty and could only
ever show a value by manually linking it to a different, live account — and the link was undone by the
next sync.

**Fix.** The Finanzguru export carries no balance column, so the import genuinely cannot know the
balance and still creates the account archived. What was wrong is that the owner could not then give it
one. `SetManualBalanceAsync` gated on `Provider == "manual"`, and the endpoint maps that refusal to a
409, so the one connection-less account kind that is not literally called "manual" was locked out. The
gate is now the **bank connection** — which is what its own comment always said it was about — and
anchoring an import account with a balance activates it, includes it in net worth and marks its bookings
as balance-history-relevant, exactly as confirming an attached history does for a live account.

Reconciliation and re-import now only re-archive an import account that has **no balance of its own**.
A bare history container still stays out of net worth; an anchored one survives every sync.

**Verified.** 288 account/portfolio/net-worth/analytics/import tests, including three new ones: anchor
an imported account with no link → visible, counted, bookings count towards history; the same account
re-imported → still active; a reconciliation pass → still active. The existing test that asserts the
archived state at import time still holds, because that state is still correct before anchoring.

### P0-5 `docker compose down -v` could delete the encryption key — `DONE` (docker 87000ff)

The app and cloud stacks both declared `fullworth-platform-secrets` as their own volume. It holds
`data_encryption_key`; without it every encrypted column is unreadable and no Postgres backup helps. It
is `external: true` in both stacks now, so compose can never remove it.

---

## P1 — core-path defects and security

### P1-1 Re-registration hands an anonymous caller someone else's instance — `OPEN` (cloud)

`FullWorth.Cloud.Api/Endpoints/InstanceEndpoints.cs:18` — `POST /v1/instances/register` carries no
instance auth filter, and re-registering an existing `instanceId` **revokes the live credential** and
issues a fresh one to the caller. Public enrollment is now on, so this is reachable from the internet:
anyone who learns an instance id can lock that instance out and take its place.

**Target.** Re-registration of a known id must require the current credential, or must create a new
identity instead of rebinding the existing one. Rate-limit and audit both paths.

**Tests.** Register, then re-register the same id anonymously → rejected, original credential still
valid.

### P1-2 The Cloud rate limiter treats the whole internet as one caller — `OPEN` (cloud)

`FullWorth.Cloud.Api/Program.cs:59` configures `UseForwardedHeaders` with no `KnownProxies` or
`KnownIPNetworks`, so `X-Forwarded-For` from Caddy is silently ignored and every unauthenticated
request partitions into **one** bucket keyed by Caddy's container IP. One caller can exhaust the
registration window for everybody.

**Target.** Trust the proxy explicitly (its container network), so the client IP is the real one.

**Tests.** Two different `X-Forwarded-For` values behind the trusted proxy get separate buckets; an
untrusted source cannot spoof the partition.

### P1-3 Every external instance's contributions are swallowed as duplicates — `OPEN` (cloud)

`FullWorth.Cloud.Infrastructure/Persistence/CloudDbContext.cs:117` makes submission idempotency keys
globally unique instead of unique **per instance**. As soon as a second instance reports the same
content-derived observation, it is discarded — which also caps the `DistinctInstances` counters the
entire consensus model is built on.

**Target.** Composite uniqueness `(InstanceId, IdempotencyKey)`.

**Tests.** Two instances submitting the same observation both count; the same instance submitting twice
counts once.

### P1-4 External instances can never verify a knowledge pack — `NEEDS DECISION`

`Modules/Intelligence/KnowledgePackModels.cs:24` ships `OfficialPublicKeyPem = ""`. Every pack sync of
every external instance fails with `knowledge_pack_public_key_missing`, forever, and the key is
currently distributed only through the owner's own Docker volume. Worse,
`KnowledgePackSyncService.cs:79` downloads the pack (up to 5 MB) *before* resolving the key, so a keyless
instance re-downloads and discards it every 5 minutes — about 288 times a day.

**Decision needed from the owner:** pin the official Cloud's public key into the constant at release
time (it is a public key; the doc comment says this was the intent), or publish it from the Cloud API.

**Independent of that decision, fix now:** resolve the key before fetching anything and fail fast.

### P1-5 A self-hoster's own Cloud URL is silently ignored — `OPEN`

`Modules/Intelligence/FullWorthCloudClient.cs:480` reads `FullWorthCloud:BaseUrl` and then discards it
unless the environment is Development or Testing. Someone who points their instance at their own Cloud
keeps sending to `api.fullworth.de` with no error and no warning. This breaks the product's own rule
that no deployment may depend on the owner's infrastructure.

**Target.** Honour the configured URL in every environment. Keep the public default; log the resolved
endpoint once at startup.

**Tests.** With `FullWorthCloud:BaseUrl` set in Production, enrollment and upload target that host.

### P1-6 Multi-currency accounts lose every wallet but one — `OPEN`

`Modules/Accounts/AccountsModule.cs:97` exposes exactly one `BalanceView` per account and picks the row
by a sort key with **no currency discriminator**; `BankSyncService.cs:1208` writes one snapshot per
currency with an identical `CapturedAt`. So for PayPal, Wise or Revolut the displayed balance is an
arbitrary wallet, it can flip between syncs, and the money in every other currency is invisible. There
is no multi-wallet model anywhere: one account has exactly one `Currency` column.

**Target.** An account carries a set of balances, one per currency, with a deterministic display rule
(the account's declared currency first, then descending value). Net worth counts all of them.

**Tests.** A PayPal account with EUR, USD and IDR wallets shows all three, sums correctly, and the
displayed primary balance is stable across syncs.

### P1-7 Linking an account to a portfolio deletes its real balance — `OPEN`

`Modules/Parity/InvestmentNetWorthService.cs:37` unconditionally removes a linked account's bank
balance from every aggregate and substitutes the portfolio's trade-derived value — which is 0 when no
trades were imported and 0 when the portfolio currency has no FX rate.

**Target.** A link must not be able to reduce a known balance to zero. Prefer the portfolio valuation
only when it is complete, and never drop the account's own balance silently.

**Tests.** Link an account with a 5 000 balance to an empty portfolio → net worth does not fall to 0
and the incompleteness is surfaced.

### P1-8 A provider balance that fails to parse becomes a real zero — `OPEN`

`FullWorth.Banking/Services/BankSyncService.cs:1602` — `GetDecimal` returns `0m` for a missing or
unparseable amount, and that 0 is persisted as a genuine current balance, indistinguishable from a real
zero. `FinTsInvestmentSnapshotEndpoints.cs:123` has the same shape: a holding with no unit price is
valued at 0 and the resulting incompleteness flag is dropped before the snapshot is written.

**Target.** Absent is not zero. Skip the row and record the failure, or persist the balance as unknown.

**Tests.** A provider payload with a missing amount produces no balance row and a visible sync warning.

### P1-9 The net-worth sparkline subtracts one currency from another — `OPEN`

`wwwroot/ui/dashboard.js:262` flattens `/api/net-worth/history` — which returns one row **per currency**
per day — into a single series. With any foreign account the chart zigzags between currencies and the
change badge subtracts, say, IDR from EUR while labelling the result in the base currency.

**Target.** Convert per row and then aggregate per day, or request an already-aggregated series.

### P1-10 A non-EUR space gets its home screen converted into EUR — `OPEN`

`Modules/Analytics/AnalyticsModule.cs:662` hard-defaults the target currency to EUR instead of the
space's base currency and the frontend never passes one, so `ui/dashboard.js:291` shows subtotals that
collapse to 0 and are mislabelled EUR.

### P1-11 The only path that gives an account a balance has no test — `OPEN`

`Modules/Ingestion/IngestionModule.cs:257` (`InsertBalancesAsync`) is the single production code path
that gives an imported or synced account its balance, and it has **zero** coverage in all four test
projects. Everything in P0-2, P0-4, P1-6 and P1-8 runs through it.

### P1-12 Foreign accounts silently vanish from the Wealth trend — `OPEN`

`Modules/Portfolio/Wealth/WealthModule.cs:481` drops every non-base-currency snapshot row whose date has
no FX rate, and the rate table is backfilled only 60 days. On the default 12-month window that means ten
of twelve months exclude every foreign account, so the curve reads flat or plainly too low.

### P1-13 A FinTS connection that needs a TAN cannot be repaired at all — `OPEN`

Three defects in one dead end, all in the path the owner asked about specifically ("no error may leave
the UI looking like nothing happened"):

1. A background or manual sync that hits a TAN sets the connection to `TAN_REQUIRED` and stores the
   challenge. Health then reports `reauthorization_required`, whose only button is Reconnect - and
   `wwwroot/features/accounts.js` `reconnectConnection()` **never checks `connection.provider`**. It
   starts an Enable Banking authorization carrying the FinTS connection id, which rewrites `Provider`
   to `enable-banking`, or with no EB profile it opens the Enable Banking setup wizard. The
   BankReauth notification points at the same button.
2. A manual sync ending in a TAN returns `ManualSyncStatus.Error` with `LastError=FINTS_TAN_REQUIRED`,
   so the user gets the generic sync-error toast with no hint that a TAN is waiting and no way to
   answer it.
3. There is no UI anywhere to enter a TAN outside the initial connect dialog.

**Target.** `reconnectConnection()` dispatches on the provider. A stored FinTS challenge is surfaced
as its own health state with a "TAN eingeben" action that reopens the challenge, and a manual sync that
ends in a TAN reports that state rather than a generic error.

**Acceptance.** A FinTS connection in `TAN_REQUIRED` shows what is waiting, lets the user answer it,
and never changes its provider.

**Tests.** Manual sync → TAN required → the connection stays `provider=fints` and the API exposes the
challenge; the reconnect action for a FinTS connection does not call any Enable Banking endpoint.

### P1-14 The same IBAN connected twice is counted twice — `OPEN`

Account uniqueness is `(FullWorthSpaceId, Provider, IdentificationHash)`, so an ING Girokonto reached
through both FinTS and Enable Banking becomes **two accounts with two transaction sets**, both counted
in net worth. The only reconciliation code that exists handles the `finanzguru-import` provider.
Documentation claimed this was prevented; nothing prevents it.

**Target.** A cross-provider identity (normalised IBAN) that either merges or refuses the second
connection, with the user told which.

### P1-15 Deleting a FinTS connection leaves the depot data behind — `OPEN`

`BankConnectionStore.DeleteForUserAsync` with `deleteLocalData=true` removes accounts, balance
snapshots and transactions, but the `InvestmentPortfolios`, `InvestmentTrades` (`Source=fints_snapshot`),
`Securities` and `SecurityPrices` rows written by `FinTsInvestmentSnapshotEndpoints` stay. The user asks
for everything to be deleted and keeps a portfolio that still counts in net worth.

**Tests.** Delete with local data → no orphaned portfolio, trade, security or price row remains.

---

## P2 — misleading, or secondary paths

- **Sums never state their rate or its date.** `WealthModule.cs:106`/`:29` expose only a boolean
  `IsComplete`, while the FX snapshot silently accepts a fixing up to 14 days old. The owner's rule is
  that a cross-currency total must say which rate and which date it used.
- **Account subtotals silently omit what they cannot convert.** `wwwroot/features/accounts.js:126`
  counts an unconvertible foreign account as zero and still prints a confident base-currency figure.
- **Historical net worth rewrites itself.** `NetWorthSnapshotService.cs:254` re-derives the accounts
  component every six hours by back-casting today's balance through today's account set instead of
  storing it as of its date.
- **The history back-cast is offset by pending authorisations.** `NetWorthSnapshotService.cs:124`
  anchors on `interimAvailable` but walks back over booked transactions only.
- **An asset valuation overwrites the current value unconditionally.**
  `AssetValuationModule.cs:154` — no check that the valuation is newer, and a differently-denominated one
  silently relabels the asset's currency.
- **The user cannot tell what a balance means.** `AccountsModule.cs:66` drops the provider's balance
  reference date, never exposes the balance type (available vs booked), and labels the sync time as
  "Datenstand".
- **The balance-type preference is implemented seven times, two different ways** — and no test covers
  any type beyond `closingBooked`/`closingAvailable`/`manual`.
- **A wallet-level sync failure aborts the whole connection.** `BankSyncService.cs:924` — one failing
  balances endpoint leaves every remaining account showing stale values.
- **The account currency defaults to EUR** when the provider omits it (`BankSyncService.cs:1104`), and
  nothing reconciles it against the currencies that actually arrived.
- **The import stamps EUR on every row** whose currency column is missing or unrecognised
  (`ImportParityModule.cs:99`) and never checks the row currency against the target account.
- **The cashflow forecast picks a different balance than the account list** for the same data
  (`CashflowParityModule.cs:222`).
- **The Wealth allocation donut splits a converted total using unconverted ratios**
  (`wwwroot/features/networth.js:688`).
- **The demo's PayPal account is seeded flat.** `ShowcaseWorldBuilder.cs:77` gives it a fixed 85.40 EUR
  and no transaction ever references it, so the shipped public demo reproduces the reported symptom.
- **The transaction drawer offers "Bankdetails" for transactions that have none.** It renders for every
  non-manual transaction; a finanzguru-import transaction has no `BankConnectionId`, so the call 404s
  and the toast shows the bare string "404".
- **Dead code: the three-slot sync schedule.** `Services/Scheduling/BankSyncScheduleService.cs` and its
  options are registered in no container and referenced only by their own tests, while the real worker
  is a plain interval loop. Either wire it in or delete both.
- **Automatic Enable Banking registration state is in-memory only** (20-minute TTL), so after a restart
  the wizard polls a 404 forever instead of failing the step.
- **Tenant isolation is 1 293 hand-threaded parameters with no enforcing layer** — every query must
  remember to filter by space, and nothing structural catches a miss.
- **Cloud enrollment leaves no trace.** A refused external enrollment writes no audit event and no log
  line, and a successful one does not record which mode let it in.
- **The Cloud has no link-health surface.** `/intelligence/index.html` is the only page with transport
  diagnostics and it is reachable only by typing the URL; the resolved Cloud endpoint appears in no
  response, no UI field and no log; outbox depth and dead-letter count are exposed nowhere.
- **A failed enrollment reads as success.** `wwwroot/intelligence/cloud.js:136` overwrites the returned
  error with a green success line, and `features/access-setup.js:571` discards the response entirely.
- **Cloud transport errors are shown as raw snake_case tokens** with no translation and no remediation.
- **Registration-on-demand runs inside user-facing GET handlers** with a 45 s timeout
  (`CloudBenchmarkEndpoints.cs:44`), so an unreachable Cloud stalls page loads.
- **No assembly version is set anywhere**, so every instance reports `clientVersion 1.0.0.0` and the
  manifest's `MinimumClientVersion` gate would lock out all instances at once.
- **Two Codex configuration namespaces coexist** (`CodexTest:*` and `AiAccess:CodexBridge*`) and
  different consumers read different ones.
- **The Cloud services have no healthchecks** — including the API, which is the stack's only
  Caddy-routed upstream. Needs `curl` in the image first.
- **Test coverage gaps** the owner named explicitly and that genuinely have nothing: import of an IDR or
  any non-EUR account end to end, PayPal, an account without an asset link, several accounts in several
  currencies, historical values under a missing rate, a non-EUR base currency through the real
  endpoints, and any frontend currency behaviour at all.

---

## P3

- Negative or zero account balances are counted in the Wealth hero figure but excluded from the
  composition donut, so one page shows two different asset totals (`features/networth.js:698`).
- The Cloud admin Instances view drops `registeredAt`, which the API already returns, and nothing
  records whether an instance is externally hosted.
- `latest` in the landing repo moves for pre-releases, against the platform's own tag policy.
- The demo repo's own `compose.yml` still pins the dead split images `fullworth-backend`/`fullworth-web`
  at `1.2.0-rc.9`; its tests assert on that file, so changing it needs the tests changed with it.
- The `Finance*` → `FullWorth*` rename is incomplete in the domain vocabulary (`FinanceAccount` 218
  references, `FinanceCategory` 252, `FinanceTransaction` 185).

---

## Owner decisions outstanding

1. **Knowledge-pack public key** (P1-4): pin into the release, or publish from the Cloud API.
2. **Benchmark anchor level per occupation** — 15 of 17 anchors were verified too low; re-anchoring
   changes what every user sees.
3. **Landing CSP** `'wasm-unsafe-eval'` for the WASM compensation calculator.
4. **Demo deploy** — the gateway fixes and the unified-image migration are ready but the public demo has
   not been redeployed.
5. **Two tracked `.env` files with live credentials** (`docker-scheduler-ui`, `caddy`) — deleting them
   from the tree does not remove them from history; the credentials need rotating.
