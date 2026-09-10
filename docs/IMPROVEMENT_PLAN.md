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
| `alpha.18` | Sync-skip reason codes, receipt-poll backoff, O-3 pending ordering, O-7 company car on gross | shipped |
| `alpha.19` | The parallel round: O-4 + O-5 (reversible IBAN-free account link), O-6 (contract merge and the projection curve), O-9 (statement import, backend and UI), O-10 step 1 (the Altersvorsorge area), balance provenance and balance meaning, FX rate provenance, the PWA offline shell and safe-area fixes, the Cloud error contract and link-health surface, version stamping | an account can be de-duplicated and undone; the three "Weg" contracts merge; the projection is a curve in the first chart; Ikano can be kept current from its own statement; the Altersvorsorge area can be filled in by hand; a balance says what it is, when it is from and where it came from; the installed app works offline |
| `alpha.20` | bAV step 2 per [PENSION.md](PENSION.md): document upload, extraction and the review screen | a statement fills a contract instead of being typed in |
| `alpha.21` | bAV step 3: the wealth block, the salary link, the dashboard entry, the projection and the variant comparison | the pension shows up in the wealth and salary views without a projection ever counting as today’s money |
| `alpha.22` | The dialog rework per [UI_AUDIT.md](UI_AUDIT.md), steps 1–4 | the editors and the booking filter stop being walls of fields |
| `alpha.23` | UI_AUDIT steps 5–6: one dialog stylesheet, tokens instead of the ~220 hex literals, and the guard | the dialogs look like one product |

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

### O-4 An imported account must be able to get a balance without being connected — `DONE`

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

The second half is done too, together with O-5 below — it is one mechanism, described there.

### O-5 A PayPal account cannot be linked, only a Giro account — `DONE`

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

**Fixed** with one mechanism for both items: `FinanceAccount.DuplicateOfAccountId` plus
`IncludeInNetWorthBeforeLink` (migration `20260910230000_AccountDuplicateLink`), and
`GET`/`PUT`/`DELETE api/accounts/{id}/link`, owner-gated with the same not-found → forbidden → conflict
ordering as the manual-balance write.

`ImportLinkedAccountId` was checked first and deliberately **not** reused: it means "this Finanzguru
archive was merged into that account", and `FinanzguruAccountReconciliationService` keeps **moving**
bookings along it on every sync. A counting link must move nothing, so it needed its own column.

What the link is: a statement about counting, nothing else. The secondary keeps every booking and
balance, stays in the list, and only leaves the totals (`IncludeInNetWorth=false`, which every
aggregation already honours). The row's existing `duplicateOfAccountId`/`duplicateOfDisplayName` now
have two sources, and a new `duplicateLinkExplicit` says which: a user decision wins over the automatic
IBAN match, and only a user decision offers "Verknüpfung aufheben". Unlinking restores the **stored**
previous flag — an account the IBAN rule had already excluded at creation goes back to excluded, it is
not guessed back to counted.

Three rules the implementation enforces rather than assumes: no chains or cycles (a duplicate cannot
become someone else's original and vice versa), the account that keeps counting must actually be counted
(otherwise "once" would be "never"), and switching a linked account back on via `PATCH` drops the link
instead of leaving the row counted *and* marked as not counted. The automatic IBAN rule is unchanged and
still fires only at creation, so no sync can overrule the owner.

Also fixed on the way: the accounts screen's group subtotal summed every row regardless of
`IncludeInNetWorth`, so a duplicate stayed counted twice in the header right above the totals that
excluded it.

Two residuals, both named on purpose:

- Hard-deleting the account that keeps counting (only possible by disconnecting its bank *with* "delete
  all data") drops the link by `ON DELETE SET NULL` and leaves the survivor excluded until the owner
  switches it back on. It is visible in the list and one click away; the alternative — cascading — would
  delete a full account with all its bookings, which is never acceptable.
- `TransferDetection` is untouched. It also compares `IbanLookup`, but for a different question: which
  two *bookings* are the two sides of one transfer. Teaching it the account link is its own change, and
  it does not affect what is counted in net worth.

### O-8 A bank missing from the picker looked unsupported — `DONE`

Reported while diagnosing Ikano. Enable Banking's `/aspsps?country=..&service=AIS` returns only the
institutions the calling **API application** is enabled for, so with a private application a bank that
Enable Banking fully supports is simply absent from FullWorth's picker until it has been added there.
The picker rendered "Keine Einträge" for that, which reads as "this bank is not supported" — the one
thing it does not mean, and the reason the owner went looking for a FullWorth bug.

**Fixed**: the empty state (and a search that matches nothing) now says that only institutions enabled
in your own Enable Banking application appear, and links straight to the API Applications page.

### O-6 Contracts cannot be merged in practice, and the wealth projection is only a tile — `DONE`

Two things.

**Merging — `DONE`.** Both suspected causes were real, and a third one sat behind them.

*Cause.* Four places compared currency strings for exact equality — the candidate list in
`contracts.js`, `ContractMergePreviewService`, `ContractStore.MergeForUserAsync` and the
`/api/contract-parity/merge` pre-check — so a contract row without a currency (older imports and
hand-written rows) could not be merged with a EUR one, from any entry point. On top of that the only
entry points were one contract at a time, and the preview always picked the survivor itself, so even a
successful merge could not keep the row the owner wanted.

*Fix.* One rule, shared: an unknown currency does not vote (`ContractMergeCurrency.TryResolve`), two
known codes are still a conflict and the refusal names both. A survivor whose own currency was unknown
adopts the one code the selection knows — no amount is touched — because payment matching filters
bookings by the contract's currency and would otherwise find nothing. `ContractMergePreviewRequest`
gained `PreferredCanonicalContractId`, which the execution service passes on so a user-chosen survivor
revalidates against its own preview token. The contract list has a selection mode (one toolbar toggle,
rows become checkboxes, "n ausgewählt" + Zusammenführen) that feeds the existing merge dialog, which now
also asks which contract stays. Merging goes through `merge-preview` + `merge-execute` (the path
`insights.js` already uses), not the legacy parity endpoint. Nothing is deleted: the merged-away rows keep
their `MergedIntoContractId` and their payment account, so they still appear under "Zahlungskonten &
Historie" and their bookings keep counting towards the surviving contract.

*Verified.* `ContractMultiMergeTests` (9 tests) walks the three-account "Weg" case, including the
currency-less row as the chosen survivor, and asserts that all three bookings, both former accounts and
every contract row survive the merge. Each of the four fixes was reverted individually to watch the
matching test fail.

**Projection.** `DONE` — the preview is the trend curve continued past today, inside card 1. The
measured history keeps its solid line and its area fill; the forward part is a dashed segment on the
**same value scale**, behind a "today" divider, with no fill — so it cannot be read as a second,
measured series. Under the chart sits its legend: the projected end value and the assumption in plain
sight ("Annahme: 600 € pro Monat · 5 % pro Jahr"), with the controls one disclosure deeper — horizon
(including "Aus", stored as `years: 0` in the unchanged `wealth.projection` preference), savings rate,
return and inflation, each moving the curve on every keystroke while the caret stays in the field. The
scrubber tells the halves apart: a projected point gets a dashed marker, reads out in the preview line
as "gerechnet, nicht gemessen", and never touches the headline net worth or the metrics. The separate
text tile is gone; the three things only it could say (purchasing power, paid in vs. growth, the
disclaimer) moved into the disclosure, so there is one representation instead of two.

Time stays honest: both halves share one pixels-per-day scale until the horizon would squeeze the
measured window below half the card width, and a custom window that ends in the past draws no preview
at all (it says why) instead of stretching the axis by ten years. Found and fixed on the way: an
unknown (`null`) net worth passed the old `Number.isFinite(Number(point.netWorth))` filter as a **zero**
— it entered the trend delta as a rise from nothing and would have anchored the projection at 0 — so
every reader of a net-worth figure now goes through `measuredValue()`.

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

### O-9 A statement (MT940 / CAMT) import had a backend but no way to reach it from the app — `DONE`

Ikano's own account, and any bank a private Enable Banking application is not enabled for, have no live
connection at all — O-4's balance anchoring cannot help them until something can hand FullWorth the
bank's own export. `ImportParityModule.UploadStatementAsync` read MT940 and CAMT into the same
job/candidate tables the CSV import uses and decided when a statement's closing balance may anchor the
account (`StatementBalanceAnchor`), but nothing in the import centre called any of it.

**Built.** A third flow in `/settings/import` (`data-import-mode="statement"`): pick the file, see what
the backend found (which account the file names, how many bookings, the closing balance and its date),
pick the **existing** account it belongs to — never a new one, choosing an account is mandatory — review
and deselect bookings, commit, and read what happened to the balance in plain German: applied, or one of
the three reasons it was not (a newer provider or manual balance, or a currency mismatch). It is a fixed
format, so it never goes through the CSV/XLSX column-mapping step. The job lands in the same
`api/import-jobs` table as every other import, so it shows up in "Letzte Buchungsimporte" and can be
rolled back like any other — the history filter was widened so a balance-only statement (legitimately
zero bookings) is not mistaken for a failed import and hidden.

Reachable directly at `/settings/import?mode=statement` and, with an account already in mind, at
`/settings/import?mode=statement&accountId={id}` — a normal query-string link the accounts page (or
anywhere else) can point at without any wiring on this page's side.

### O-10 There is no place for the occupational pension (bAV) — `PARTLY DONE` (step 1 of 3)

The owner's bAV had no home. The balance could only be typed in as a nameless `Asset`, which loses the
implementation route, the policy holder vs the insured person, the guarantee, the annuity factor and —
worst — the employer/employee split, so a 338 € contribution made of 169 € + 169 € looked like a 338 €
outflow. There was also nothing to stop next year's statement from overwriting this year's value.

**Step 1 — DONE.** The architecture is [PENSION.md](PENSION.md); it was written before any code and is
what got built. Six new tables (`BavContracts`, `BavSnapshots`, `BavContributions`,
`BavInvestmentAllocations`, `BavCosts`, `BavDocuments`) in migration
`20260910233000_OccupationalPension`, `/api/pension/*`, and the Altersvorsorge area
(`features/pension.js`, view `pension`) with Übersicht / Verträge / Verlauf and a working manual entry
flow. Nothing is duplicated: the balance reaches net worth through one `Asset` of kind
`insurance_pension` per contract, and the employee payment is meant for the existing
`RecurringContract`.

Five invariants are enforced in the store **and** as database checks, so they hold for a writer that
bypasses the API:

- the same policy number at the same provider is one contract — a create for it is a 409 naming the
  existing contract, so an annual statement becomes a snapshot;
- history is added to, never rewritten: `(contract, date, document)` is unique with
  `NULLS NOT DISTINCT`, so a second value for a date is a 409;
- `paid_up` (beitragsfrei) is its own status next to `active`, and every cost carries
  `ContinuesWhenPaidUp` — beitragsfrei is neither cancelled nor cost-free;
- a tax or social-insurance effect needs the source that stated it (`document` / `payslip` /
  `simulation`); FullWorth computes no personal net effect of its own;
- a projected figure needs its basis and its return assumption, and only a snapshot **balance** ever
  reaches the asset — a projection never becomes wealth.

The policy number is personal data: encrypted with `FieldCipher`, matched through a keyed blind index
like an IBAN, and returned only as its last four characters. No provider is hardcoded anywhere.

**Verified.** 18 backend integration tests in `tests/FullWorth.Backend.Tests/Pension/` and 3 shell/UX
guards in `tests/FullWorth.Web.Tests/PensionUxBaselineTests.cs`, plus `FinanceMigrationTests` (the model
snapshot still matches the model) and the frontend guard suite.

**Open.** Step 2: document upload, extraction and the review screen. Step 3: the `pensionAssets` block
in the wealth overview, the employee share in cashflow, the dashboard entry, the projection and the
variant comparison. `PENSION.md` lists both, plus the append-only limitation of contributions/costs.

---

## P2 — misleading, or secondary paths

- **Sums never state their rate or its date.** `PARTLY DONE` — every component now reports the
  currencies **it** could not convert (`WealthComponentView.MissingCurrencies`), and the wealth page
  names the value and the rate together ("Unvollständig, weil ein Wechselkurs fehlt: Konten (IDR)")
  instead of a flat "something is incomplete" with a list of currencies detached from any figure. Still
  A converted total now also says what it was converted **with**: `FxSnapshot.ConvertToBase` returns the
  effective rate and the fixing date behind it, a cross-rate through EUR reports the **older** of its two
  fixings (a total is only as current as its stalest input), and `WealthComponentView.RatesUsed` carries
  one entry per currency with its age and a `IsStale` flag past `FxSnapshot.StaleAfterDays` (4 days —
  a weekend plus a holiday). The 14-day lookback deliberately stays: shortening it would turn
  conversions that work today into missing numbers, which trades a stated uncertainty for no answer at
  all. Writing the test caught a real regression on the way — routing the plain conversion through the
  new one made an amount **already in the base currency** report "unconvertible", which would have marked
  every total containing base-currency money incomplete.

  Still open: the frontend does not show the rate and its date yet (the wealth page is being edited in
  parallel), so the data reaches the API but not the screen.
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
- ~~**An asset valuation overwrites the current value unconditionally.**~~ `DONE` — both halves. The
  currency half: accepting a valuation denominated in another currency is refused with a reason
  (relabelling a 400 000 EUR house as 400 000 of something else is not a conversion, and a conversion is
  derived and may never overwrite the original). The asset's currency is no longer in the UPDATE at all;
  only an asset that had none yet receives one.

  The date half needed its own bug fixed first, in `20260911003000_AssetValuationAsOfProvenance`: the
  `fullworth_prepare_asset` trigger stamped `ValuedAt = CURRENT_DATE` whenever a row was touched without
  one, so a stamp that never described an appraisal was indistinguishable from one — and always looked
  newer. The two facts are now separate. `Assets."ValuedAt"` holds **only** a date somebody stated and
  stays NULL when nobody did; `Assets."ValueRecordedAt"` (trigger-maintained, moves only with value,
  currency or stated date) and `AssetValuations."CreatedAt"` say when FullWorth learned the figure;
  `AssetValuations."ValuedAtIsStated"` marks which of the two a history row's date is. Existing rows were
  migrated conservatively — a date within a day of the row's own last-touched day is treated as the stamp
  it almost certainly was (dropped on the asset, `ValuedAtIsStated = FALSE` on the valuation, nothing lost
  because `ValueRecordedAt` carries that same day honestly), and `legacy` rows are only promoted when the
  asset's surviving date proves it. Only then the accept rule: accepting a valuation whose **stated** date
  is older than the **stated** date of the current one is refused with both dates in the message. A
  current value with no stated date is not evidence, and an undated valuation means "as of now", so
  neither can make real input stale — the flow in `RealEstateAdvancedIntegrationTests` still passes, and
  fails immediately if the stamp is put back. Recording anything with `isAccepted=false` still keeps it in
  the history.
- ~~**The user cannot tell what a balance means.**~~ `DONE` — the reference date and the provenance
  landed earlier; the missing half was that a row still never said whether the figure was the **booked**
  balance or the **available** one, which differ by exactly the pending authorisations the reader is
  trying to account for. `CurrentBalances.Meaning` derives that from the balance type
  (`available`/`booked`/`expected`/`recorded`) and `BalanceView` ships it as a computed `meaning`, so no
  caller can stamp a figure with something its type does not say. `ui/balance-meaning.js` turns it into
  one muted word under the amount ("verfügbar"/"gebucht"/"erwartet", full sentence on hover) on the
  accounts list, the overview account rows and the net-worth account rows — deliberately NOT a
  client-side copy of the type table. A manual or imported anchor gets **no** label: it has no
  booked/available claim to make, and the row already says "manuell erfasst"/"aus Import".
  "Datenstand" is now used only for `referenceDate`, a date somebody actually vouched for; a row that
  only has `capturedAt` says "Abgerufen", because when FullWorth wrote a figure down is not a data date.
- ~~**The balance-type preference is implemented seven times, two different ways**~~ `DONE` — earlier
  work had already folded most callers into `CurrentBalances`; three copies were left, in two shapes:
  the int `Rank` switch, the string-CASE ordering in `BalanceSnapshotQueries.CurrentFirst`, and a
  hand-written duplicate of that CASE inlined into the accounts-list EF projection. All three are gone.
  There is now ONE table (`CurrentBalances.Preference`) that `Rank`, `IsBooked` and `Meaning` are all
  read off, so the preference, "is this settled money" and the label the user sees cannot disagree.
  No EF projection needs a copy any more, so nothing had to be generated or asserted-equal: the
  projection's subquery was **removed** (its result was overwritten by `WithAllCurrenciesAsync` anyway,
  and both callers of `Project()` run that), and `CurrentFirst`'s last caller —
  `FinancialReconciliationReports.CashflowAvailableAsync` — reads `CurrentBalances.LoadAsync`, which also
  fixes the wallet defect it still had (one row per account, so a multi-currency account contributed a
  fraction of its money to "available today") and drops an N+1. The coverage gap is closed too:
  `BalanceTypePreferenceTests` (36, no DB) and `BalanceMeaningApiTests` (13) cover `interimAvailable`,
  `interimBooked`, `expected`, unknown types, `manual`/`manualCurrent`, the `import` source, every
  permutation of a single capture, and the case that matters most — several types with the SAME
  `CapturedAt`, where the row must not flip type or wallet between two syncs of the same data.
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
- ~~**Automatic Enable Banking registration state is in-memory only**~~ `DONE` — and staying in memory is
  the right call, not the defect: a pending registration holds a control-panel refresh token and a
  freshly generated private key, and neither belongs in the database for the sake of a twenty-minute
  wizard step the owner can simply start again. The defect was the answer. An id this process no longer
  holds now reports `status: "expired"`, `errorCode: "registration_lost"` instead of a 404, so the wizard
  fails the step and says why instead of polling a 404 for as long as the page stays open. It also
  removes the 404-versus-200 distinction between "never existed" and "expired", so another user's
  in-flight registration is indistinguishable from one that was never there.
- **Tenant isolation has no enforcing layer** — `NEEDS DECISION`. Measured on 2026-09-10: **51** entity
  types carry a `FullWorthSpaceId`, **690** query sites filter on it by hand, **1 327** method
  signatures thread the space through, and there are **0** EF global query filters. Every query has to
  remember, and nothing structural catches a miss. Test coverage for the failure mode is thin and
  uneven: 26 files assert cross-space isolation at all, concentrated in Purchases, Accounts,
  Transactions and Security, while Analytics, Budgets, Categories, Compensation, Merchants,
  Notifications, Pension and Tax have none.

  Not attempted in this pass, deliberately: it is a change to every read path in the product, and
  making it while eight other workstreams were editing the same files would have been reckless. It also
  needs a decision that is not the implementer’s to make, because the options differ in what they cost
  and in what they break:

  1. **EF global query filters** driven by an ambient space accessor. The real fix, and the only one
     that makes a forgotten filter impossible. But background workers, the ingest path and the
     cross-space admin surfaces legitimately read across spaces, so every one of those needs an
     explicit `IgnoreQueryFilters()` — and each of those becomes a place where the guarantee is off
     again, only now silently. Largest change, strongest guarantee.
  2. **A dev/test EF interceptor** that fails a query touching a space-scoped table with no space
     predicate. Catches the bug class without changing production behaviour; costs nothing at runtime
     because it is not enabled there. Weaker: it only catches what a test actually exercises, which is
     the same coverage problem measured above.
  3. **Close the coverage gap only** — a cross-space isolation test per module, no architectural
     change. Cheapest, proves the current state is correct, prevents nothing in future code.

  Options 2 and 3 compose, and together they are a credible answer without touching 690 query sites.
  Option 1 is the only one that is structural. The choice is the owner’s.
- ~~**Cloud enrollment leaves no trace.**~~ `DONE` (fullworth-cloud) — a refused enrollment (missing or
  invalid token, or a fail-closed deployment) is now audited as `instance_enrollment_refused` and logged
  as a warning; an accepted one records which of the three enrollment gates let it in
  (`CloudInstance.EnrollmentMode`: `Token` / `PublicRegistration` / `DevelopmentBypass`) in both the audit
  row and the log line. Neither ever logs the presented token or an issued credential.
- ~~**The Cloud has no link-health surface.**~~ `DONE` — three separate blind spots. The resolved
  endpoint now reaches the state the page reads and is logged once at startup (a misconfigured
  `FullWorthCloud:BaseUrl` warns instead of crashing, so a self-hoster who never uses the Cloud still
  boots); the outbox reports waiting count, dead-letter count and the oldest waiting item’s age, which
  is the number that says whether the link works at all; and the page is no longer reachable only by
  typing its URL — the Cloud Intelligence wizard, where an operator already is when they want to check,
  links to it. The endpoint is configuration, not a secret; no credential, token or fingerprint goes
  near it.
- ~~**A failed enrollment reads as success.**~~ `DONE` — enabling Cloud Intelligence stores the decision
  and then registers, and registration is deliberately best-effort (a temporarily unreachable Cloud must
  not make setup fail). But the green "ist aktiviert" line was printed either way, so a refused or
  unreachable Cloud read as success while the reason sat unnoticed in `lastErrorCode`. The page now
  checks it and says the decision was saved but the registration failed, with the reason and the fact
  that it retries by itself. The Cloud API was verified to be correct here — it already returns a real
  non-2xx with a reason — so the whole defect was on this side.
- ~~**Cloud transport errors are shown as raw snake_case tokens**~~ `DONE` — two halves. The Cloud sends
  `{errorCode, message, remediation}` with every error and this client threw the body away, deriving a
  code from the HTTP status alone: every 403 became the same generic `cloud_entitlement_denied` and the
  Cloud’s own advice was lost. It reads the contract now, keeps the status-derived code as the fallback
  for a body it cannot parse (a reverse proxy answering instead of the Cloud sends HTML), refuses a
  "code" that is really a sentence, and keeps transience a property of the status. The page then turns
  the code into a German sentence with a next step instead of printing the token — an unknown code is
  still shown, because a token is more use than silence.
- ~~**Registration-on-demand runs inside user-facing GET handlers**~~ `DONE` — six read endpoints each
  carried the same twenty-five lines (no secret → register → save → record the transport status), and
  registration goes through the client's 45 s HTTP timeout, so an unreachable Cloud stalled a page load
  for up to 45 s — **once per request**, because every request started its own attempt. One
  `CloudCredentialAcquisition` now does it with a 5 s budget and a one-minute cooldown, so the first
  load gives up quickly and the next answers at once. Nothing is lost by giving up: the background
  workers register too, so a Cloud that comes back is picked up without a reload. Each endpoint kept its
  own failure response, because they legitimately differ (503 versus an `available:false` body).
- ~~**No assembly version is set anywhere**~~ `DONE` — `Directory.Build.props` sets the version
  (`0.0.0-dev` locally, the real tag in a release), the three .NET Dockerfiles take it as a build arg and
  copy the props file into the build context, and `release.yml` passes the validated tag to both
  architectures. The nine hand-rolled `Assembly.GetName().Version` readers became one
  `FullWorthVersion`: `Full` reports the prerelease label (which is what distinguishes two alphas) with
  the commit suffix stripped, `Numeric` is what the knowledge-pack minimum-client check compares — and a
  build that set no version can no longer be judged "too old", which would have broken a self-hoster
  building from source.
- ~~**Two Codex configuration namespaces coexist**~~ `DONE` — and it was worse than untidy: the AI
  provider resolvers read `AiAccess:CodexBridge*` with a fallback to `CodexTest:*`, while the receipt
  bridge, the payslip extractor and the receipt test endpoints read `CodexTest:*` **only**. So an operator
  who configured just the newer namespace got the AI providers and silently lost receipt scanning and
  payslip extraction, with nothing saying why — and the deploy stack had to export the same secret twice
  under two names to work at all. One `CodexBridgeConfiguration` reads it now:
  `AiAccess:CodexBridge*` is canonical, `CodexTest:*` still works so a running deployment keeps running,
  and the host logs once which deprecated key is carrying the configuration (**key names only** — the
  bridge key is a secret). Default stays off, and a non-http base URL is refused rather than retried.
- ~~**The Cloud services have no healthchecks**~~ `DONE` (fullworth-cloud) — the API and Web images now
  install `curl` and declare a `HEALTHCHECK` combining `/health` (liveness, no database) and `/ready`
  (readiness, database-checked; Web needed its own because it authenticates operators against its own
  `CloudAdminUser` table). The worker has no HTTP surface, so `WorkerHeartbeatService` touches two files
  instead (`alive` every tick, `ready` only after a successful database ping) that the image's
  `HEALTHCHECK` reads via `find`. The deploy repo's `docker-compose.yml` wires the same checks in, and
  `fullworth-cloud-web` now depends on `fullworth-cloud-api` with `condition: service_healthy` instead of
  `service_started`.
- ~~**Test coverage gaps** the owner named explicitly and that genuinely have nothing: import of an IDR or
  any non-EUR account end to end, PayPal, an account without an asset link, several accounts in several
  currencies, historical values under a missing rate, a non-EUR base currency through the real
  endpoints, and any frontend currency behaviour at all.~~ `DONE` — every item now has a test that asserts
  the RULE rather than the current output. Backend, all through the real endpoints:
  `Import/NonEuroAccountImportIntegrationTests` (an IDR MT940 uploaded and committed: amounts stay in
  rupiah, the account carries its own balance with no asset and no linking step, net worth names IDR
  instead of guessing 1:1 or 0, and a dot-decimal amount in a foreign CSV is not multiplied by a
  hundred), `Accounts/WalletAccountCurrencyTests` (a PayPal-shaped wallet across two syncs: the headline
  pick does not move, no wallet is lost, and a wallet without a rate stays visible while the total says
  it is incomplete), `Accounts/AccountWithoutAssetLinkTests` (a bare account's value on the list, the
  single read, the dashboard, the overview and the trend, plus a hand-entered balance refused in a
  foreign currency), `Portfolio/NonEuroBaseCurrencyIntegrationTests` (an IDR-based space with EUR/USD/IDR
  and one unconvertible account: totals, account list, wealth overview, dashboard, income/expenses and a
  duplicate link counted once) and `Portfolio/WealthHistoryHistoricalRateTests` (today's rate neither
  fills in nor lowers a day two months back, a rate backfilled FOR that date resolves it at that date's
  value, and reading the trend never rewrites the stored native snapshot). Frontend:
  `FullWorth.Web.Tests/CurrencyUiBaselineTests` covers the three behaviours named — own-currency
  rendering, the converted figure as a second line that never replaces the original, and an
  unconvertible value marked rather than dropped — plus the balance dialog's currency and the privacy
  mask keeping its symbol. Shared seeding lives in
  `tests/FullWorth.Backend.Tests/Infrastructure/CurrencyScenario.cs`. Note the standing repo-wide limit:
  there is no browser/e2e harness, so the frontend assertions read the shipped assets rather than a
  rendered DOM.

---

## P3

- ~~**Two test processes destroyed each other's template database.**~~ `DONE` — `BackendWebApplicationFactory`
  dropped a **fixed-name** template on every process start, so a second test process (a second working
  tree, or a filtered run beside a full one) dropped the first one's template mid-run and every test in
  it died on `relation "AmazonConnections" already exists` or "database does not exist". A large part of
  what this repo treats as local test flakiness was this. The build is now serialised by a PostgreSQL
  advisory lock, the template name carries a fingerprint of the migration set, and a finished build is
  reused instead of rebuilt — so processes on the same schema share one template and processes on
  different schemas never touch each other's. A template that exists without its ready marker was
  interrupted and gets rebuilt.

- Negative or zero account balances are counted in the Wealth hero figure but excluded from the
  composition donut, so one page shows two different asset totals (`features/networth.js:698`).
- ~~The Cloud admin Instances view drops `registeredAt`~~ `DONE` (fullworth-cloud) — the admin UI now
  shows `registeredAt` and a derived `externallyHosted` flag, honestly sourced from the newly tracked
  `EnrollmentMode` (only `PublicRegistration` is reachable from outside the deployment's own Docker
  volumes); an instance that registered before this was tracked shows unknown rather than a guessed value.
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
