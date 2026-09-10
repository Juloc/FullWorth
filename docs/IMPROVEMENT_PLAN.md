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

## Release slices

The work is cut so that every few items end in a released alpha the owner can deploy and test, instead
of one long branch. A slice ships when its items are green; anything that turns out bigger than the
slice moves to the next one rather than holding the release.

| Release | Contents | What the owner can check |
| --- | --- | --- |
| `alpha.17` | The P0/P1/P2 sweep up to and including the FX refresh fix | shipped |
| `alpha.18` | Sync-skip reason codes, receipt-poll backoff, O-3 pending ordering, O-7 company car on gross | the log says why a sync skipped; "Vorgemerkt" sits above "Heute"; the salary graph counts the car in Brutto |
| `alpha.19` | Balance provenance (source, as-of, note), manual balance, Finanzguru import balances, unlinked import accounts counting in net worth | an imported or unconnected account shows a real balance and says where it came from |
| `alpha.20` | Statement import (CSV/MT940/CAMT) into an existing account, explicit reversible account link/unlink — O-4 second half and O-5 | Ikano and PayPal can be kept current without a live connection, and a duplicate can be resolved and undone |
| `alpha.21` | O-6: contract merge and the wealth projection as a graph | the three "Weg" contracts become one; the projection is a curve, not a tile |
| `alpha.22`–`alpha.24` | bAV per [PENSION.md](PENSION.md): domain and manual flow, then document import and snapshots, then wealth/salary/dashboard/simulation | the Altersvorsorge area, filled by hand first and from a statement afterwards |

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

### P0-2 Net worth adds foreign-currency balances at face value — `DONE`

**Cause.** `Modules/Portfolio/NetWorthSnapshotService.cs:218` buckets each account's latest
`BalanceSnapshot.Amount` by the **account's** `Currency` and never reads the snapshot's own `Currency`.
The same mistake sits in `Modules/Analytics/AnalyticsModule.cs:225` and `:240` for the dashboard.

**Impact.** A balance reported in a currency other than the account's declared currency is summed 1:1
into the wrong bucket and then FX-converted with the wrong rate. `WealthModule` does it correctly off
the snapshot's own currency, so the same data produces **two different net-worth numbers** depending on
which surface you look at.

**Fix.** The dashboard now selects `Amount` AND `Currency` from the same balance row - one correlated
subquery, not two, because a sync stamps every balance type with an identical `CapturedAt` and two
subqueries could disagree about which row they read. The materialized history buckets by the balance
row's currency instead of the account's, only lets bookings in that same currency move the anchor, and
opens a bucket for a currency no account declares, so that money can no longer vanish from the series.

**Verified.** Proven both ways: with the fix reverted the new test reports `accounts = 110` for a
110 USD balance on a EUR-declared account at rate 1.10; with it, `100`. 297 money-path tests pass.

### P0-3 The headline net worth ignores every loan and every portfolio — `DONE`

**Cause.** `Modules/Analytics/AnalyticsModule.cs:301` computes the Overview net-worth and "available"
tiles from a rule that never reads `db.Loans` and never reads investment portfolios.

**Impact.** The number the user sees first is **overstated by every mortgage and loan** and understated
by every portfolio. It disagrees with the Wealth page, which does read them.

**Fix.** The dashboard reads `db.Loans` (active only) into liabilities and takes the portfolio
contribution from the same `InvestmentNetWorthService` the history uses - including its
`ExcludedLinkedAccountIds`, so an account linked to a portfolio is counted once and not twice, and its
`Incomplete` flag folds into the dashboard's.

**Verified.** With the fix reverted the new test reports `liabilities = 0` for a 300 EUR mortgage; with
it, `300`, and a settled (inactive) loan stays out.

**Still open in this area:** the Overview tile and the Wealth page remain two implementations of the
same definition. They now agree on loans, portfolios and currencies, but the duplication is the reason
they diverged and is worth collapsing into one aggregate (tracked as a P2 refactor).

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

### P1-1 Re-registration hands an anonymous caller someone else's instance — `DONE` (cloud 34175bd, client b6e41b7)

`FullWorth.Cloud.Api/Endpoints/InstanceEndpoints.cs:18` — `POST /v1/instances/register` carries no
instance auth filter, and re-registering an existing `instanceId` **revokes the live credential** and
issues a fresh one to the caller. Public enrollment is now on, so this is reachable from the internet:
anyone who learns an instance id can lock that instance out and take its place.

**Fix.** Registration creates an identity. Re-registering a known id requires proof that the caller
holds one of that instance's credentials, and both outcomes are audited. The proof is deliberately
weaker than authentication - an expired or already-revoked credential still counts - because that is
what a legitimate instance renewing itself holds, while someone who only learned an id holds nothing.
New code `instance_already_registered` (409).

Client half: the held credential is presented on re-registration, and the 401 self-heal now renews with
the rejected credential instead of deleting it first, which would have left the instance unable to
re-enroll at all.

**Verified.** Proven by reverting: the takeover test expects 409 and the old code answered 200. Six new
cloud tests plus twelve client-side cases.

**Not yet deployed:** image 1.2.6 predates this fix.

### P1-2 The Cloud rate limiter treats the whole internet as one caller — `DONE` (cloud a566377)

Both hosts called `UseForwardedHeaders` with no known proxy, and the built-in known set is loopback
only, so `X-Forwarded-For` from Caddy was dropped: the API's anonymous partition collapsed into one
bucket keyed by Caddy's container address, and the admin UI's 10/min login throttle became a single
shared bucket a stranger could exhaust to lock the operator out.

`CloudProxyTrust` now resolves the trust set for both. Default: every private range plus loopback (the
container network the deployment's own proxy runs on) — a public peer is never trusted, so an exposed
port cannot be used to pick a partition. `Cloud:Proxy:TrustedProxies`/`TrustedNetworks` give an explicit
set instead, `ForwardLimit` stays at one hop because Caddy appends the real client, and a malformed
entry throws at startup. No deploy-stack change needed.

### P1-3 Every external instance's contributions are swallowed as duplicates — `DONE` (cloud 46c4f5d)

Idempotency keys were unique across the whole deployment, but clients derive them from the content they
observed, so two instances that saw the same fact send the same key by construction. The second instance
got `duplicate`, and no event, observation or candidate evidence was written — so the more instances
agreed on a fact, the less of it could reach the `DistinctInstances` thresholds promotion and every
benchmark bucket depend on.

Now unique on `(InstanceId, IdempotencyKey)`, with the existence check scoped the same way; a single
instance repeating a key is still a duplicate. Batch receipts had the same shape of bug (keyed on
`BatchId` alone, so another instance's receipt was replayed and its own events dropped) and are now keyed
`(InstanceId, BatchId)`. `CloudDynamicLearningSchemaUpgrade` migrates existing databases.

### P1-4 External instances can never verify a knowledge pack — `NEEDS DECISION`

`Modules/Intelligence/KnowledgePackModels.cs:24` ships `OfficialPublicKeyPem = ""`. Every pack sync of
every external instance fails with `knowledge_pack_public_key_missing`, forever, and the key is
currently distributed only through the owner's own Docker volume. Worse,
`KnowledgePackSyncService.cs:79` downloads the pack (up to 5 MB) *before* resolving the key, so a keyless
instance re-downloads and discards it every 5 minutes — about 288 times a day.

**Decision needed from the owner:** pin the official Cloud's public key into the constant at release
time (it is a public key; the doc comment says this was the intent), or publish it from the Cloud API.

**Independent of that decision, DONE (96daf74):** the key is resolved before any request, so a keyless
instance no longer downloads and discards up to 5 MB every five minutes.

**Also DONE:** the key can now be obtained at all - the Cloud admin UI shows it with a download and a
copy button (cloud a14f5d6). What remains is the owner's decision on where it gets pinned.

### P1-5 A self-hoster's own Cloud URL is silently ignored — `DONE` (96daf74)

`Modules/Intelligence/FullWorthCloudClient.cs:480` reads `FullWorthCloud:BaseUrl` and then discards it
unless the environment is Development or Testing. Someone who points their instance at their own Cloud
keeps sending to `api.fullworth.de` with no error and no warning. This breaks the product's own rule
that no deployment may depend on the owner's infrastructure.

**Fix.** The configured URL is honoured in every environment. Outside Development it must be HTTPS and
a public host - a loopback or private address there is a copied development setting, and failing loudly
beats posting to a host that answers nothing.

**Verified.** Proven by reverting: the test asks for `https://cloud.example.org` and the old code
answered `https://api.fullworth.de`. 12 resolution cases.

**Still open:** the resolved endpoint is still not surfaced anywhere (see the visibility items in P2).

### P1-6 Multi-currency accounts lose every wallet but one — `DONE`

`Modules/Accounts/CurrentBalances.cs` is now the single selection rule, and it separates the two things
that were tangled together: **per (account, currency)** the newest capture wins (with the balance-type
preference as the tiebreak), and **per account** one of those currencies is shown first — a display
decision that no longer decides which money counts. The headline is the declared currency, then the
largest holding, so it cannot flip between syncs.

Every surface reads it: the account list (`balances` per row, `baseValue` now covering the whole
account), the dashboard total, the wealth overview and its emergency-fund reserve, the net-worth
history — whose back-cast anchor is now per (account, currency) — and the data export. The rank helper
that was copied into four files is gone.

Frontend: the account row and the dashboard account row list the other wallets under the headline
amount. Verified in `ops/ui-harness`: the PayPal row reads `100,00 €` with `2.000.000 IDR · 55,00 $`
beneath it and `250,00 €` as the base-currency total.

Proven by reverting to one balance per account: totals read 100 instead of 250, and the headline
currency came back as USD on one surface and IDR on another — the instability, visible.

### P1-7 Linking an account to a portfolio deletes its real balance — `DONE` (668dc32)

The account is now excluded only when the portfolio produced a usable valuation: it has trades, no
price or rate was missing, and the conversion into the base currency succeeded. Otherwise the account
keeps its own balance and that portfolio contributes nothing, so nothing is double counted either way,
and the result is flagged incomplete. Per-portfolio incompleteness is tracked per portfolio instead of
in one flag shared across all of them.

The Wealth page had the mirror-image bug on the client: it hid every account named by any portfolio, so
a row could vanish while the headline still contained it. The overview now reports
`accountsRepresentedByDepots` from the same calculation that produced the totals.

### P1-8 A provider balance that fails to parse becomes a real zero — `DONE` (490a25a)

The amount reader returns null instead of `0m`. An unreadable balance row is skipped and counted; if any
were skipped the account reports `BALANCE_UNREADABLE`, which puts the connection in the error health
state instead of showing a clean sync that found no money. An unreadable transaction is skipped rather
than booked as 0 — its fingerprint key had made that 0 permanent.

FinTS depots: the unit price is derived from the market value the bank reported when no price was sent,
so a depot the bank valued at 40 000 is no longer worth 0; a reported price still wins, and a position
with neither stays unpriced and reports incomplete. Found while testing it: a holding **without an
ISIN** failed the entire depot snapshot with a 500 (untyped NULL parameter in the security lookup).

### P1-9 The net-worth sparkline subtracts one currency from another — `DONE`

The dashboard now reads `api/wealth/history` — one already-converted point per day in the target
currency, the same series the Wealth page draws — over a 12-month window, and skips the days that
report unknown instead of drawing them as 0. Verified in `ops/ui-harness`: the widget draws a
sparkline and a change badge of exactly +7 200 (the fixture rises 600 a month for twelve months);
before it drew nothing at all there, because no fixture answered the raw endpoint.

### P1-10 A non-EUR space gets its home screen converted into EUR — `DONE`

The requested currency stays optional all the way down and `AnalyticsService.ResolveCurrencyAsync`
falls back to the space's base currency — dashboard, overview, forecast and chart. An explicitly
requested currency still wins.

Noted while fixing it: `budget-status` looks like it is served by `AnalyticsModule`, but
`BudgetReconciliationCompatibility` is a middleware in front of that path and answers it instead — and
it already resolved the base currency. Pinned by a test, because nothing in the code makes that
shadowing visible.

### P1-11 The only path that gives an account a balance has no test — `DONE`

`tests/FullWorth.Backend.Tests/Ingestion/BalanceIngestionTests.cs` covers it through the real endpoint:
every wallet of a multi-currency account is stored, a balance keeps the currency it was reported in, a
later sync appends history instead of overwriting the earlier value, and a balance for an unannounced
account is ignored rather than attached to another account.

### P1-12 Foreign accounts silently vanish from the Wealth trend — `DONE`

Two halves. The rate table is now deep-backfilled across the history window (`Fx:HistoryBackfillDays`,
default 400) while it does not already reach that far, so a fresh install can convert a foreign account
on an old day at all; afterwards only the cheap 60-day window is refetched. `FxRateBackfill.ResolveFrom`
is pure and tested, including the weekend slack that would otherwise re-fetch everything every cycle.

And a day that still cannot convert one of its rows now reports **unknown** rather than a partial sum:
a total missing a whole account is not a smaller net worth, and the chart already drops null points and
draws a gap. `WealthHistoryPoint.NetWorth` became nullable for that reason.

### P1-13 A FinTS connection that needs a TAN cannot be repaired at all — `DONE`

All three dead ends are closed. `tan_required` is its own health state, so the row offers **TAN
eingeben** instead of Reconnect (which would have started a fresh authorization and discarded the
challenge the bank is waiting for). `reconnectConnection()` dispatches on the provider, so a FinTS
connection re-authorizes with its own login and can no longer be rewritten to `enable-banking`. A
manual sync on a TAN-pending connection returns `tan_required` — checked *before* the authorization
test, which used to answer "reconnect needed" — and the UI opens the challenge straight away. A new
`GET api/banking/fints/connections/{id}/challenge` reads the parked challenge back (challenge only;
the login and PIN stay in the service), which is what made answering a TAN outside the original
dialog possible at all.

Verified in `ops/ui-harness`: the healthy connection shows the sync button, the FinTS one shows "TAN
nötig" and "TAN eingeben".

The original analysis, for the record:

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

### P1-14 The same IBAN connected twice is counted twice — `DONE`

Owner decision: **keep both connections, count the money once.** Refusing the second connection would
have blocked the owner's own ING setup (FinTS for the depots, Enable Banking for the bookings), and
merging two transaction sets with different keys risks deleting or duplicating real bookings.

A newly created account whose `IbanLookup` matches an existing active, counted account in the same space
starts with `IncludeInNetWorth = false`, and that is audited. Only at creation: an account the user
deliberately switched back on is never silently switched off again by the next sync. Every total already
filters on that flag, so nothing else had to change — and the flag is the toggle.

The account list marks the excluded row with `duplicateOfAccountId`/`duplicateOfDisplayName` (computed
from the keyed lookup token, which never leaves the server) and the row reads "Doppelt zu X · zählt
nicht im Vermögen" — without that, an account missing from net worth had no visible reason.

Proven by reverting: the dashboard total comes back as 2 000 for 1 000 of real money.

### P1-15 Deleting a FinTS connection leaves the depot data behind — `DONE`

The delete now also removes the depot the connection created: portfolios whose `ProviderName` carries
this connection id (trades cascade with them), and the securities that nothing else uses — a security
that is also on a watchlist, a portfolio benchmark, a benchmark definition, referenced by a broker
import or still traded anywhere survives, because deleting a bank connection must not quietly destroy
unrelated investment history. Prices cascade off the security.

The SQLite test harness gained the tables this path reads (it hand-creates a subset of the raw-SQL
parity schema), and the SQL avoids `DELETE ... AS alias` and array parameters so it runs on both
providers.

---

## Reported by the owner on 2026-09-10

Seven items from use, in the priority they get worked. Each was checked against the code; what is
stated as confirmed was read in the source, and what needs data from the running instance says so.

### O-1 PayPal bookings all sit on the sync date instead of their real booking date — `NEEDS DATA`

Checked and ruled out: the ingest never invents a date (`IngestionModule.cs:419` assigns exactly what
arrived), the transaction table renders `bookingDate` and shows `—` when it is null (`dateHeading`), and
the booking-activity chart groups by `BookingDate ?? ValueDate`. So nothing in our code substitutes the
sync time — which means the provider payload for these rows is the place to look.

**What is needed:** one PayPal row from the instance — its `BookingDate`, `ValueDate`, `FirstSeenAt` and
the `booking_date`/`value_date` fields of its stored `RawJson` (encrypted at rest, so it has to come from
the running app). If PayPal reports only a value date, the fix is to fall back to it *visibly* rather
than leaving the date empty; if it reports neither, the row must say "date unknown" instead of adopting
any timestamp.

### O-2 Pending bookings are never resolved when they book or are cancelled — `DONE`

`IngestionModule.UpsertTransactionsAsync` only ever inserts or updates. A pending row is keyed
`fp:<fingerprint including status>` (Enable Banking rarely gives a stable `entry_reference` for pending),
so when the same payment returns as `BOOK` it arrives under a DIFFERENT key and is inserted as a second
row. Nothing ever deletes the pending one, and nothing links it to its booked successor. A cancelled or
expired authorisation stays forever.

So every pending payment ends up as two rows, and any view that includes pending counts the money twice.

**Fixed** exactly that way. A complete account sync now sends a `PendingReconciliation` (the pending
keys the provider still reports plus the window start); the ingest removes the pending rows that are no
longer in it and lie inside the window, and audits each as `transaction.pending_resolved`. A run that hit
the page limit sends nothing, because a truncated history cannot tell a booked row from one it never
reached. Rows the user has touched - a note, a manual category, a refund or transfer link, a linked
purchase - are left alone: losing what the user entered is worse than a leftover row.

### O-3 The "today" bar sits above the newest pending row in the booking history — `DONE`

The list ordered purely by booking date, so a pending row dated today landed *underneath* the "Heute"
header among real bookings, and one with no booking date at all opened an unlabelled `—` group *above*
it (PostgreSQL sorts NULLs first on a descending order). The header therefore marked neither today nor
the booked/not-booked boundary.

**Fixed** in the ordering, not the display: the default date sort puts every pending entry ahead of every
booked one and orders within each block by `BookingDate ?? ValueDate`, so an entry whose booking date the
bank has not published yet sits at its real position instead of at the very top. The leading pending run
gets its own "Vorgemerkt" header, which makes the "Heute" header below it mean today again, and a pending
row with no booking date shows its value date instead of an em dash.

### O-4 An imported account must be able to get a balance without being connected — `PARTLY DONE`

The first half is done. P0-4 opened the server side: `PUT api/accounts/{id}/balance` accepts a balance for
any account without a bank connection, a `finanzguru-import` account included, and anchoring one makes it
active, counted in net worth and gives its bookings a balance history — no link to another account
involved.

The frontend had been left on the old gate, which is why the owner still saw those accounts reported with
`Balance unavailable` and `IncludeInNetWorth=false`. Two gates, both now fixed: the list hid every
archived account (an import account is created archived, because the Finanzguru export carries **only
bookings — there is no balance column in the file**, so nothing is discarded at creation, there is
nothing to discard), and the balance affordance was rendered only for `provider === 'manual'`. So the
account existed, carried its history, and had no reachable path to a balance anywhere in the app. It is
listed now, marked "Kontostand hinterlegen", and offers the balance action.

Still open: when an imported account is later matched with a real connected one, the **duplicate has to
be resolvable — and the resolution has to be reversible and changeable**. P1-14 built the detection and
the "count it once" rule for the same IBAN across providers, but nothing lets the user say "these two ARE
the same, merge them" or undo that decision afterwards.

### O-5 A PayPal account cannot be linked, only a Giro account — `OPEN` (cause found, ships with O-4)

The first guess was wrong and is corrected here: **no account picker filters by account type.** The
contract payment account, the depot settlement account, the loan account, the emergency-fund scope and
the manual-booking account all offer every account, wallet accounts included.

The real blocker is that every path which decides "these two are the same account" is keyed on the IBAN
token alone:

- `IngestionModule.cs:209` only sets `IbanLookup` when the provider reported an IBAN, and the
  count-once rule below it is inside `if (isNew && ibanLookup is not null)`.
- `AccountsModule.WithDuplicateMarkersAsync` filters to `account.IbanLookup != null` before looking for
  a counterpart, so a wallet row can never even be told which account it duplicates.
- `TransferDetection.cs:216` matches the two sides of a transfer the same way.

PayPal, Wise, Revolut, cash and manual accounts have no IBAN, so for them linking and de-duplication do
not exist at all — not because a list filtered them out, but because the identity these paths use is one
they cannot have. The fix is therefore the same mechanism O-4 still needs: an explicit, user-chosen and
reversible link between two accounts that does not depend on an IBAN. Both ship together.

### O-8 A bank missing from the picker looked unsupported — `DONE`

Reported while diagnosing Ikano. Enable Banking's `/aspsps?country=..&service=AIS` returns only the
institutions the calling **API application** is enabled for, so with a private application a bank that
Enable Banking fully supports is simply absent from FullWorth's picker until it has been added there.
The picker rendered "Keine Einträge" for that, which reads as "this bank is not supported" — the one
thing it does not mean, and the reason the owner went looking for a FullWorth bug.

**Fixed**: the empty state (and a search that matches nothing) now says that only institutions enabled
in your own Enable Banking application appear, and links straight to the API Applications page.

### O-6 Contracts cannot be merged in practice, and the wealth projection is only a tile — `OPEN`

Two things.

**Merging.** The machinery exists and is reachable (`openMergeDialog` from the contract detail and from
the duplicate banner, `ContractMergeExecutionService` behind it), but the owner has three separate
contracts for what was only an account change and cannot merge them. Two candidate causes are visible in
the code: the candidate list is filtered to an EXACTLY equal currency string
(`contracts.js:1020`, so a contract with no currency can never be merged with a EUR one), and the only
entry points are one contract at a time — there is no "select these three, merge" in the list. Needs a
reproduction against his three contracts to say which.

**Projection.** The wealth page already computes a projection (`buildProjectionCard`, the
`wealth.projection` preference), but shows it as a text tile. It belongs IN the first graph: a
configurable forward preview of how net worth could develop, drawn as a continuation of the trend.

### O-7 The salary graph shows the company car separately instead of on top of gross — `DONE`

The chart drew the car as a series of its own — and not even the gross-relevant figure: it plotted the
net *cash* impact, a small, often negative line that flattened the whole scale (which is why it shipped
switched off by default).

**Fixed** by putting the taxable benefit where payroll puts it. The timeline now reports
`CompanyCarTaxableBenefitAnnual` (the geldwerter Vorteil actually applied that year) and
`GrossIncludingCompanyCarAnnual`; the Brutto curve, the "Brutto aktuell" metric and the year table's
Brutto column all read the latter, and the separate Firmenwagen curve and its toggle are gone. The
baseline, the Kaufkrafterhalt line and the nominal/real percentages sit on the same basis, so a car added
to an unchanged salary now shows as the gross increase it is instead of "nothing happened" — and a car
present from the start is not mistaken for a raise. `ContractualGrossAnnual` keeps its meaning (cash
gross) and the hover readout plus the metric break out "davon Firmenwagen".

---

## P2 — misleading, or secondary paths

- **Sums never state their rate or its date.** `PARTLY DONE` — every component now reports the
  currencies **it** could not convert (`WealthComponentView.MissingCurrencies`), and the wealth page
  names the value and the rate together ("Unvollständig, weil ein Wechselkurs fehlt: Konten (IDR)")
  instead of a flat "something is incomplete" with a list of currencies detached from any figure. Still
  open: a total that WAS converted does not say which rate and which date it used, and the FX snapshot
  still silently accepts a fixing up to 14 days old.
- **A balance never said where it came from.** `DONE` — `BalanceSnapshot` carries `Source`
  (`provider`/`manual`/`import`) and the owner's `Note`, and a manual balance finally uses
  `ReferenceDate` as the owner's as-of date instead of always stamping today. So an account anchored
  from last month's statement reads as last month's figure and says it was entered by hand. A future
  as-of date is refused rather than clamped.
- ~~**Account subtotals silently omit what they cannot convert.**~~ `DONE` — a subtotal that had to
  leave money out is marked (`*` with the reason on hover) on the accounts page and on the dashboard.
  Adding the foreign figure into a base-currency total is still refused; it is the silence that was the
  defect. An account with no balance at all is not "incomplete" — it simply has no value yet.
- ~~**Historical net worth rewrites itself.**~~ `DONE` — a snapshot written on (or before) the day it
  describes is a measurement and is now kept; the back-cast only fills days that have none, and
  **re-anchors** on a measured day so earlier days derive from what was recorded there instead of from
  today's balance carried across it. `IsObservedSnapshot` was too loose to protect a measurement with (a
  one-day slack for UTC skew also lets a row reconstructed today for yesterday look measured), so this
  uses its own stricter predicate. Today stays live.
- ~~**The history back-cast is offset by pending authorisations.**~~ `DONE` — the walk now continues
  from the **booked** balance of the same (account, currency) while today keeps the preferred figure
  the user sees. Anchor and deltas describe the same money again; before, every past day was off by
  whatever was pending. Falls back to the preferred balance when the provider sent no booked type.
- **An asset valuation overwrites the current value unconditionally.** `PARTLY DONE` — the currency half
  is fixed: accepting a valuation denominated in another currency is refused with a reason (relabelling a
  400 000 EUR house as 400 000 of something else is not a conversion, and a conversion is derived and may
  never overwrite the original). The asset's currency is no longer in the UPDATE at all; only an asset
  that had none yet receives one. Recording it with `isAccepted=false` still keeps it in the history.

  **The date half is deliberately NOT fixed, and the reason is a bug of its own:** there is no
  trustworthy "as of" date to compare a valuation against. The `fullworth_prepare_asset` trigger stamps
  `ValuedAt = CURRENT_DATE` whenever an asset row is touched without one, and creating an asset
  materialises a "current" valuation carrying that same synthetic date. So a legitimate appraisal dated
  last month looks *older* than a stamp that never described an appraisal — a naive check rejects real
  input (it broke `RealEstateAdvancedIntegrationTests` on exactly that flow). Fixing this properly means
  separating "when this was appraised" from "when this row was last touched" first, and only then
  refusing a stale accept.
- **The user cannot tell what a balance means.** `AccountsModule.cs:66` drops the provider's balance
  reference date, never exposes the balance type (available vs booked), and labels the sync time as
  "Datenstand".
- **The balance-type preference is implemented seven times, two different ways** — and no test covers
  any type beyond `closingBooked`/`closingAvailable`/`manual`.
- ~~**A wallet-level sync failure aborts the whole connection.**~~ `DONE` — a provider error on one
  account is caught, logged and reported as `ACCOUNT_SYNC_FAILED`, and the remaining accounts still
  sync. Connection-level categories (rate limit, consent/session expired, auth required) still abort
  the run on purpose: continuing would hammer the provider and every remaining account would fail the
  same way. The split uses the existing `EnableBankingErrorClassifier`.
- ~~**The account currency defaults to EUR** when the provider omits it.~~ `DONE` — the parser no
  longer invents one: `AccountState.Currency` is nullable, and the sync resolves it from the currency
  the money actually **arrived** in. A balance or booking with no currency anywhere is treated as
  unreadable rather than stamped with a guess. EUR survives only as the last resort when the provider
  named no currency AND sent no readable balance, and that case is logged.
- ~~**The import stamps EUR on every row** whose currency column is missing or unrecognised.~~ `DONE`
  in both importers (`ImportParityModule` and `ImportMappingParityModule`): a currency column that IS
  present but unreadable makes the row a visible error instead of being relabelled, and a file with no
  currency column falls back to the **space** base currency rather than a hardcoded EUR. Not done, and
  deliberately: comparing the row currency against the target account. A foreign-currency booking on a
  euro account is legitimate (a card payment abroad), so a mismatch is not by itself an error — what
  would help is showing the mix before the commit, which is a UI question.
- ~~**The cashflow forecast picks a different balance than the account list** for the same data.~~
  `DONE` — it reads `Accounts.CurrentBalances` like every other surface, so the newest capture per
  (account, **currency**) with the balance-type preference as the tiebreak. It used to order by
  `CapturedAt` alone (a sync stamps every type identically, so the pick was arbitrary) and to read one
  currency per account, forecasting a wallet account from a fraction of its money. Also two queries
  now instead of one per account.
- ~~**The Wealth allocation donut splits a converted total using unconverted ratios.**~~ `DONE` — the
  wealth overview returns `realEstateAssets`, a subset of `manualAssets` converted with the same rates,
  and the chart uses it instead of deriving a share from native asset values. Measured in the harness
  with a 9 000 EUR house next to a 5 000 000 IDR asset: the house showed as **21,56 € (0 %)** before
  and 9 000,00 € (17 %) after.
- **The demo's PayPal account is seeded flat.** `ShowcaseWorldBuilder.cs:77` gives it a fixed 85.40 EUR
  and no transaction ever references it, so the shipped public demo reproduces the reported symptom.
- ~~**The transaction drawer offers "Bankdetails" for transactions that have none.**~~ `DONE`, and it
  was worse than described: `isManual` was **never in the API response at all**, so `!t.isManual` was
  always true — the button rendered for every transaction including manual ones, and the delete action
  for a manual transaction (guarded by the same undefined field) rendered for **none**. The row now
  carries `isManual` and `hasProviderDetails` (a provider transaction id AND a live non-FinTS
  connection), the button follows the latter, and an error without a known code shows the plain "no
  details available" sentence instead of a bare status string.
- ~~**Dead code: the three-slot sync schedule.**~~ `DONE` — deleted, with its options and its tests.
  It was registered in no container, had no `SyncSchedule` section in any appsettings, dated from the
  repo split and was referenced only by its own tests, so removing it cannot change behaviour. What it
  implemented is not lost either: the ≥6 h per-connection cadence it enforced is already enforced by
  `CanBackgroundSync`, and the worker's 5–60 min loop is only a wake-up interval. Its own default was
  `TimeZoneId = "Europe/Berlin"`, which the platform rules would have had to fix anyway. If predictable
  fixed sync times are wanted later, that is a feature request against `BankSyncWorker` — not a reason to
  keep an unreferenced second scheduler next to the real one.
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
- ~~**No assembly version is set anywhere**~~ `DONE` — `Directory.Build.props` sets the version
  (`0.0.0-dev` locally, the real tag in a release), the three .NET Dockerfiles take it as a build arg and
  copy the props file into the build context, and `release.yml` passes the validated tag to both
  architectures. The nine hand-rolled `Assembly.GetName().Version` readers became one
  `FullWorthVersion`: `Full` reports the prerelease label (which is what distinguishes two alphas) with
  the commit suffix stripped, `Numeric` is what the knowledge-pack minimum-client check compares — and a
  build that set no version can no longer be judged "too old", which would have broken a self-hoster
  building from source.
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
