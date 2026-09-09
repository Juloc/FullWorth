# Import

Every way data enters FullWorth from a file, a document store or a third-party website. Live bank
synchronisation is not an import and is described in [Banking safety](BANKING_SAFETY.md); the ingest
path it writes through is listed here only where an importer shares it.

All importers run inside the single unified container (`FullWorth.Web` + Backend + Banking in one
process on `:8080`). Nothing is offloaded to an external worker.

## The money rule

An imported value must be correct without any manual linking step afterwards. An import that leaves
the user to reconcile a balance by hand has not finished its job.

An amount is always persisted in the currency it was imported in. Base-currency figures are computed
at read time only — `AccountStore.WithConvertedBalancesAsync` fills `AccountListItem.BaseValue` /
`BaseCurrency` from an FX snapshot per request, `InvestmentNetWorthService` converts per portfolio and
then into the space base currency, and `NetWorthSnapshotService` refuses to mix a foreign booking
amount into a native account balance. No import writes a converted amount into a stored column, and a
missing FX rate is never assumed to be 1:1 — the affected line is skipped and the result flagged
`incomplete`.

Three places do not honour the rule yet; the fixes belong in the improvement plan:

- No transaction importer writes a `BalanceSnapshot`. `NetWorthSnapshotService` anchors today on the
  newest snapshot and walks backwards subtracting daily transaction deltas, so importing a year of
  bookings into an account whose balance is manual leaves *today* at the pre-import number and pushes
  the imported sum into the past instead.
- The Finanzguru importer creates its history container with `IsActive=false` and
  `IncludeInNetWorth=false` and no balance at all. A correct balance requires the separate account-link
  step in which the user types the current balance by hand.
- The depot importer writes no `SecurityPrices`, although every buy/sell row carries a price. Imported
  positions are therefore valued only once a price arrives from market data, a FinTS depot snapshot or
  manual entry; until then `InvestmentNetWorthService` skips the holding and sets `incomplete`.

## Where the user starts

| Page | Route | Sources offered |
| --- | --- | --- |
| Import center | `/settings/import` (`/settings/import/finanzguru` redirects here) | CSV/XLSX bookings, depot CSV/XLSX, links to the two provider pages |
| Finanzguru XLSX | `/settings/import/finanzguru/xlsx` | Finanzguru "Alle Buchungen" workbook, account linking |
| Broker PDF | `/settings/import/broker-pdf` | Broker confirmations, text or scanned |
| Purchases → *Belege importieren* | dialog inside `/purchases` | Bulk receipt upload, Paperless-ngx, watched folder |
| Purchases → *Amazon verbinden* | dialog inside `/purchases` | Amazon.de order history |
| OS share sheet | `POST /share/receipt` (PWA share target) | Receipt images/PDFs shared into FullWorth |

Page HTML for the three import pages lives in C# raw string literals under
`src/FullWorth.Web/Modules/Import/`; their behaviour is in
`src/FullWorth.Web/wwwroot/features/{import-center-page,finanzguru-import-page,broker-pdf-import-page}.js`.

## Transaction import (CSV / XLSX)

Two API surfaces exist over the same staging tables.

`/api/import-mapping` (`ImportMappingParityModule.cs`) is what the UI uses: `POST /detect` →
`POST /upload` → `GET /jobs/{id}/summary` → `POST /jobs/{id}/duplicate-preview` →
`POST /jobs/{id}/commit`. `/api/import-jobs` (`ImportParityModule.cs`) additionally offers a
mapping-free `POST /upload` + `POST /{id}/commit` against one fixed target account, and owns the
shared endpoints `GET /`, `GET /{id}`, `GET /{id}/candidates`, `POST /{id}/cancel` and
`POST /{id}/rollback`. The UI calls the mapping module for the wizard and the job module for the
candidate list, the history list and rollback.

**Accepted format.** `multipart/form-data`, field `file`, `.csv` or `.xlsx`, max 25 MB. CSV is decoded
as UTF-8 with an optional BOM; the delimiter is guessed from `;`, `,` and tab by frequency in the
header line; quoted fields may contain newlines. XLSX reads only `xl/worksheets/sheet1.xml` plus the
shared-string table. The first row is the header; a file with fewer than two rows yields no data rows.

**Column mapping.** `POST /detect` returns headers, a ten-row preview and a suggested mapping matched
on a normalised header name (lower-cased, non-alphanumerics stripped) against German and English
aliases: date/datum/buchungstag, amount/betrag/umsatz, currency/währung, counterparty/empfänger/payee,
description/verwendungszweck, account/konto, category/kategorie, id/booking id. The browser refines
that for the `outbank` and `finanzfluss` presets before showing the mapping grid. `mapping.date` and
`mapping.amount` are required; a mapping that names a column not present in the file is rejected.

**Parsing per row.** Dates accept an Excel serial number (20 000–100 000, epoch 1899-12-30) and the
formats `yyyy-MM-dd`, `dd.MM.yyyy`, `d.M.yyyy`, `dd/MM/yyyy`, `MM/dd/yyyy`, `yyyy/MM/dd`, then the
current culture. Amounts strip `€`, spaces and apostrophes and are parsed as `de-DE` first, invariant
second — so `1.234,56` and `1234.56` both work. Currency must be three letters or it falls back to
`EUR`. A row that fails to parse is kept as a candidate with `ValidationStatus='error'` and its
message; it is never silently dropped and never committed.

**Persisted staging.** One `ImportJobs` row (file name, SHA-256 of the bytes, adapter key
`mapped_csv`/`mapped_xlsx` or `generic_csv`/`generic_xlsx`, counts) and one `ImportCandidates` row per
source row carrying the parsed values plus a `RowFingerprint` (SHA-256 over
date|amount|currency|counterparty|description|externalKey).

**Duplicate detection** runs at commit time and is previewable, because the answer depends on the
account mapping and therefore cannot be decided at upload. `ImportMappingParityEndpoints.ClassifyAsync`
is the single classifier used by both the preview and the commit, so the review list and the commit can
never disagree. Three reasons:

- `in_file` — an earlier row of the same file already carries that external key or the same semantic key.
- `external_key` — the derived key (`mapped-import:external:<sha256(id)>`, or
  `mapped-import:fingerprint:<rowFingerprint>` when the file has no id column) already exists on that account.
- `existing` — same account, date, amount, currency and normalised counterparty.

Nothing is written by the preview; a stored status would be a guess about a mapping the user can still
change.

The mapping-free `/api/import-jobs` commit checks the same semantic tuple but derives its external key
as `import:<jobId>:<externalKey|fingerprint>`. That key is job-scoped, so re-uploading the same file
through that endpoint produces new keys and only the semantic check catches the repeat. The UI does not
use it.

**Commit.** For each non-duplicate candidate a `FinanceTransaction` is inserted with `Status='BOOK'`,
`BookingDate` = `ValueDate` = the parsed date, `Amount` and `Currency` exactly as parsed
(`numeric(20,8)`, `varchar(3)`), `NormalizedCounterparty` from `MerchantNormalization`, `ExternalKey`
as above and `RawJson` = `FieldCipher`-protected `{"source":"mapped-import"}` (`generic-import` on the
job module). Category resolution order: explicit `categoryMappings` entry → existing category whose
name matches the file's category text → newly created category (`import-<hash>` key, owner only, opt-in
via `createMissingCategories`). Only if no category was resolved and `runFullWorthCategorization` is on
does `TransactionRuleEngine.EvaluateWithGermanyCatalog` run; it may also set `IsTransfer`.
`CategorizationSource` records which of the three happened (`import`, the rule source, or `none`).
Candidates whose source account is unmapped are counted as `skipped`, not imported.

Permissions: `transactions.write` for detect/upload/preview/commit, space owner for
`createMissingCategories`, and the target accounts must be in the caller's writable set.

**Rollback.** Every created transaction id is written to `ImportTransactionLinks`
(`ImportTransactionProvenance.LinkAsync`), which is what makes an undo possible at all — a
`DuplicateStatus` says that something was imported, not what, and an `ExternalKey` prefix can be edited
later. `POST /api/import-jobs/{id}/rollback` requires a `completed`, not-yet-rolled-back job with at
least one link, and deletes only linked transactions that the user has not worked on since:
`ImportTransactionProvenance.DeleteImportedTransactionsSql` excludes any transaction referenced by
asset cashflows, contract links, price-change evidence, item returns, purchase payment links, purchase
refunds, purchases, receivable payments, refund dismissals, spending reviews, allocations, review
states, tags or a refund pointer. Cascading foreign keys would have taken the user's work with the row
and restricting ones would have aborted the whole rollback, so those rows are kept and reported as
`kept`. `ImportTransactionProvenanceGuardTests` compares that exclusion list against the live schema, so
a new table referencing `Transactions` cannot quietly fall outside it. The response is
`{jobId, removed, kept}`; the job becomes `rolled_back` and its imported candidates
`DuplicateStatus='rolled_back'`. `rollbackAvailable` in the job list is false for a job that predates
provenance tracking, so the button never promises an undo it cannot perform.

## Finanzguru workbook import

`POST /api/import/finanzguru` (`FinanzguruImportModule.cs`), `.xlsx` only, max 25 MB.

`FinanzguruWorkbookReader` validates by header name, not column position, so reordering columns cannot
corrupt money data. Required headers: `Buchungstag`, `Betrag`, `Waehrung`, `Buchungs-ID`,
`Referenzkonto`, `Name Referenzkonto`, `Beguenstigter/Auftraggeber`, `Verwendungszweck`, `E-Ref`,
`Analyse-Hauptkategorie`, `Analyse-Unterkategorie`, `Analyse-Umbuchung`, `Referenz-Original-ID`,
`Split-Typ`; `IBAN Beguenstigter/Auftraggeber` is read when present. Duplicate column names are
rejected. Only the alphabetically first worksheet is read, bounded to 100 000 rows, 64 MB of worksheet
XML and 32 MB of shared strings. A fully blank trailing row is skipped; a partially filled row is an
error, not a silent drop. `Split-Typ` must be `Original`, `Teilbuchung` or `Restbetrag`.

**Splits** are validated before anything is written: every child must have an `Original`, the children
must sum exactly to the original amount, and they must not cross account boundaries. The `Original` row
becomes the `FinanceTransaction`; each child becomes a `TransactionAllocations` row with its own
category. A parent with children gets no own `CategoryId`.

**Account resolution** (`ResolveAccountsAsync`). The source key is the whitespace-stripped
`Referenzkonto`, or `NAME:<name>:<currency>` when it is empty. A previously confirmed link
(`Accounts.ImportLinkedAccountId`) wins and survives re-imports even when the source has no usable
IBAN. Otherwise, if the reference looks like an IBAN, its last four characters plus a matching currency
must identify exactly one owned account. With no match the importer reuses or creates a
`Provider='finanzguru-import'` container account: `InstitutionName='Finanzguru Import'`,
`Product='Imported history'`, currency from the file, `IsActive=false`, `IncludeInNetWorth=false`,
`IdentificationHash=sha256("finanzguru|<sourceKey>")`. A container owned by a different user is a
`409`.

**Duplicate detection.** `ExternalKey = "finanzguru:<Buchungs-ID>"` is unique per account, so a
re-import is idempotent (`AlreadyImported`). When the rows land on a live bank account the importer
additionally consumes semantic matches — same date, amount, currency and normalised counterparty, or
normalised description when no counterparty exists — from the non-`finanzguru:`, non-`PDNG` rows in the
file's date range, one per existing row (`MatchedExistingTransactions`), so imported history does not
duplicate what the bank already delivered.

**Persisted.** `FinanceTransaction` with `Status='BOOK'`, both dates set to `Buchungstag`, `Amount` and
`Currency` from the file, `ProviderTransactionId` = `Buchungs-ID`, `EntryReference` = `E-Ref`,
`IsTransfer` from `Analyse-Umbuchung='ja'`, `UseForBalanceHistory=false`, and `RawJson` =
`FieldCipher`-protected JSON containing the full source row and its split children.
`Analyse-Hauptkategorie`/`-Unterkategorie` resolve to a parent/child category pair, created as
`finanzguru-<hash>` keys only when the caller is the space owner; unresolved pairs are counted
(`CategoriesUnmapped`) and leave the transaction uncategorised.

**Account linking** (`FinanzguruAccountReconciliationService.cs`) attaches that history to a real
account once it exists.

- `GET /api/import/finanzguru/accounts` lists the import containers, the candidate targets and history
  already attached to a target.
- `POST /api/import/finanzguru/accounts/{importAccountId}/link` moves the rows and records the
  confirmed link.
- `POST /api/import/finanzguru/accounts/{targetAccountId}/confirm-history` trusts rows a prior
  automatic reconcile already moved.

Both require the same currency on both sides, set `UseForBalanceHistory=true` on the affected rows,
deactivate the container, set `IncludeInNetWorth=true` on the target and then rebuild net-worth history
for the user. `EnsureCurrentBalanceAsync` demands a balance: if the target has no `BalanceSnapshots`
row and the request carries no `currentBalance`, it fails with "The target account has no balance. Enter
the current balance to anchor the imported history." A supplied balance is written as one
`BalanceSnapshot` — `Amount` = the value as given (`numeric(20,8)`, rejected at ≥ 1 000 000 000 000),
`Currency` = the request currency normalised to three upper-case letters and required to equal the
account currency, `BalanceType='manualCurrent'`, `ReferenceDate` = today (UTC), `CapturedAt` = now. The
browser sends it as a `number` from a `step="0.01"` input.

Automatic reconciliation also runs on every bank sync (`IngestionService` calls
`FinanzguruAccountReconciliationService.ReconcileAsync`), deliberately conservative: same space,
currency, IBAN last-4 and at least one common owner, and ambiguous matches are left alone rather than
risking a cross-account merge.

## Depot import (CSV / XLSX)

`/api/investment-import` (`InvestmentImportParityModule.cs`), capability `investments.manage`:
`POST /detect` → `POST /upload` → `GET /jobs/{id}/summary` → `POST /jobs/{id}/commit`, plus
`GET /jobs/{id}`, `GET /history`, `GET /portfolios/{id}/reconciliation` and `POST /jobs/{id}/rollback`.
Same file rules as the transaction import: `.csv`/`.xlsx`, 25 MB, same CSV/XLSX readers.

The mapping covers trade date, transaction type, settlement date, security name, ISIN, WKN, ticker,
quantity, price, gross amount, net amount, currency, fees, taxes, withholding tax, asset class and
external key; `tradeDate` and `tradeType` are required. `sourceProvider` is a value, not a column, and
only influences asset-class normalisation. The UI adds presets for Trade Republic, Parqet and
Finanzfluss and auto-detects a Trade Republic export from the header set
(`account_type, category, asset_class, type, symbol, shares, transaction_id`), then decides from the
data whether `symbol` holds ISINs or tickers.

**Type normalisation** maps German and provider spellings onto twelve canonical types: `buy`, `sell`,
`cancellation`, `dividend`, `interest`, `fee`, `tax`, `deposit`, `withdrawal`,
`security_transfer_in`, `security_transfer_out`, `split`, `other`. `REDEMPTION` becomes `sell` because
it closes a position for proceeds. `BUY_CANCELLED` becomes `cancellation`, which is not treated as a
taxable sell. `CARD_TRANSACTION` and `COMPENSATION` become `other` and are excluded from estimated cash.
Provider mergers, spin-offs and ISIN changes need richer source data than these CSVs carry and are not
guessed. Asset classes normalise to stock/etf/fund/derivative/bond/crypto/commodity/cash/other, with
Trade Republic's `fund` treated as `etf`.

**Amounts.** Quantity, price, gross and net are read as absolute values; the sign is carried by the
type, not the number. When no net amount is mapped it falls back to gross, then to price × quantity.
Fees, taxes and withholding tax default to `0`.

**Validation** per row: trade date present, type in the allowed set, three-letter currency, ISIN
exactly 12 characters if given, a security identity plus a positive quantity for
buy/sell/cancellation/transfer/split, and a positive price or gross amount for buy/sell.

**Persisted staging.** `InvestmentImportJobs` + `InvestmentImportCandidates`. The candidate fingerprint
is `sha256(semanticFingerprint|occurrence)`, where the occurrence counter makes two genuinely identical
rows in one file distinct instead of collapsing them.

**Security resolution.** Candidates are grouped by `isin:` > `wkn:` > `ticker:|currency` >
`name:|currency`. `AutoMatch` accepts an ISIN match outright and a WKN, ticker+currency or
name+currency match only when it is unique. The review step shows every group with its auto-match; the
commit can also create missing securities (`ProviderKey='investment-import'`, `IsActive=true`) when the
user opts in. A row that requires a security and has none resolved aborts the commit with the
unresolved list rather than importing a headless trade.

**Commit** runs in one `Serializable` transaction. Rows are ordered by trade date, then by type
priority (in-flows, then splits, then out-flows), then by row number, so a ledger-integrity trigger
cannot fail on ordering alone. The target is either an existing writable portfolio or a new one created
from `createPortfolio` (name plus three-letter currency, `IsManual=true`, `IncludeInNetWorth=true`).
Duplicates are detected on `ExternalKey`, which is
`investment-import:external:<sha256(id)>` or `investment-import:fingerprint:<candidateFingerprint>`,
both within the file and against existing trades in the target portfolio. Each accepted row becomes an
`InvestmentTrades` row with `Source='import'`, `Notes='Imported row <n>'`, `Quantity` and `Price` as
`numeric(24,10)` and `Amount`/`Fees`/`Taxes`/`WithholdingTax` as `numeric(20,8)` in the row's own
`Currency` — never converted to the portfolio currency. Trade ids go to
`InvestmentImportTradeLinks`, created security ids to `InvestmentImportSecurityLinks`, and the job
records `PortfolioId` and `PortfolioCreated`. Any failure returns `409` with nothing imported.

**Reconciliation** is returned by the commit and separately by
`GET /portfolios/{portfolioId}/reconciliation`, so an import immediately reports its own health. It
replays the whole ledger: quantities from buys, sells, transfers, cancellations and multiplicative
splits, and an estimated cash balance per currency from `CashImpact` (deposits add, withdrawals
subtract, buys cost amount + fees + taxes + withholding, sells/cancellations/dividends/interest credit
amount minus those, fee/tax events debit). Warnings: `negative_position` (error), `negative_cash`,
`mixed_currencies` (both warnings) and `unclassified_events` (info, `other` rows excluded from cash).
`healthy` is false only when an error-severity warning exists.

**Rollback.** `POST /jobs/{id}/rollback` needs a `completed` job with linked trades. It defers
constraints, deletes the linked trades, then deletes each import-created security only while nothing
else references it (no trades, prices, watchlist items or portfolio benchmark) and the
import-created portfolio only when it is empty afterwards. The response reports `removedTrades`,
`removedSecurities`, `keptSecurities` and `portfolioRemoved`. The deferred ledger-integrity trigger
`TR_InvestmentTrades_ValidateLedger` can only fail at COMMIT, which already aborts the transaction, so
that case is caught and returned as a clean `409` with nothing removed.

## Broker PDF import

`POST /api/investment-import/pdf/detect` (`InvestmentPdfImportParityModule.cs`) and
`POST /api/investment-import/pdf/ocr-detect` (`InvestmentPdfOcrImportParityModule.cs`), capability
`investments.manage`. Both accept a single `.pdf` up to 25 MB and verify the `%PDF-` magic bytes.

Neither endpoint writes anything. They return normalised rows plus a fixed suggested mapping; the
browser turns those rows into a semicolon-separated CSV and pushes it through the ordinary
`POST /api/investment-import/upload` → `summary` → `commit` flow, so a PDF goes through exactly the
same review, duplicate and provenance handling as a CSV. The page tries text extraction first and falls
back to OCR on failure, and also retries with OCR when the text result warns about multiple
confirmations and OCR finds more rows.

Text extraction shells out to `pdftotext`; OCR renders up to 12 pages with `pdftoppm -jpeg -r 220` and
reads each with `tesseract -l deu+eng --psm 6`. Both binaries ship in the unified image
(`poppler-utils`, `tesseract-ocr`, `tesseract-ocr-deu`). A missing binary is a `503`, not a `500`.

`BrokerPdfTradeParser` recognises Trade Republic, Scalable Capital, ING, DKB, comdirect and flatex by
name, and otherwise reports `unknown`. It detects buy/sell/dividend/interest from the first 5 000
characters, reads the trade date from `Ausführungstag`/`Handelstag`/`Schlusstag`/`Geschäftstag`/`Datum`
(falling back to the document's first date, with a warning), settlement from
`Valuta`/`Wertstellung`/`Settlement`, ISIN by pattern, WKN by label, quantity from
`Stückzahl`/`Anzahl`/`Nominale`, price from `Ausführungskurs`/`Kurs`/`Preis`, gross from
`Kurswert`/`Bruttobetrag`, and the net amount from `Ausmachender Betrag`/`Endbetrag`/
`Abrechnungsbetrag`/`Gesamtbetrag`/`Zu Ihren Lasten`/`Zu Ihren Gunsten`/`Gutschrift`/`Belastung`. Fees
sum `Provision`, `Orderentgelt`, `Transaktionsentgelt`, `Fremde Spesen` and the exchange-venue fees;
taxes sum `Kapitalertragsteuer`, `Solidaritätszuschlag` and `Kirchensteuer`; withholding tax comes from
`Quellensteuer`. The currency is taken from the amount's own label first, then gross, then price, then
the document, then `EUR`.

Confidence starts at 0.55 and rises with a known broker, a security identifier, a quantity, a price and
a labelled currency, capped at 0.95. A missing type, date or amount returns no trade at all rather than
a guess. The OCR endpoint additionally discards any page below 0.65 confidence, deduplicates identical
pages by signature and — if no single page qualifies — retries once on the concatenated text. A
document holding several confirmations produces a warning; only rows recognised safely are offered.

## Bulk receipt import

`/api/purchases/receipt-imports` (`ReceiptImportEndpoints.cs`, `ReceiptImportService.cs`,
`ReceiptImportStore.cs`). Three sources, one pipeline: every imported file is handed to the ordinary
receipt scan queue (`ReceiptScanQueueService.EnqueueProviderAsync`), which stores a durable copy under
`PurchaseStorage:RootPath` before OCR, creates the draft `Purchase` and, when `autoStart` is set,
starts analysis. Multi-page PDFs stay one purchase and are expanded by the existing rasterizer.

- **Upload** — `POST /upload`, `multipart/form-data`, files in `receipts` (or any field), `currency`,
  `autoStart`, optional `clientBatchId`.
- **Paperless-ngx** — `GET|POST|DELETE /paperless/connection`, `POST /paperless/test`,
  `GET /paperless/options`, `GET|POST|DELETE /paperless/presets`, `POST /paperless/preview`,
  `POST /paperless/import`. `PaperlessAutoImportWorker` re-runs auto-import presets on an interval.
- **Watched folder** — `GET /folder/status`, `POST /folder/preview`, `POST /folder/import`.

Batches and items are read back via `GET /batches` and `GET /batches/{id}`, and repaired with
`POST /batches/{id}/start-pending` and `POST /batches/{id}/retry-failed`. A failed Paperless or folder
item can be retried because the bytes can be fetched again; a failed browser upload cannot and says so.

**Accepted formats** are exactly the receipt families the canonical queue takes: `.jpg`, `.jpeg`,
`.png`, `.webp`, `.heic`, `.pdf`.

**Limits.** `ReceiptImports:MaxBatchItems` (500) caps items per batch, `MaxUploadBytes` (512 MB) caps
one browser upload request as a whole, and `PurchaseStorage:MaxReceiptBytes` (20 MB) caps each
individual receipt inside the receipt queue and the folder scanner. Both hosts raise their own
request-body limit for that one endpoint from the same setting — the backend in
`ReceiptImportEndpoints`, the `FullWorth.Web` BFF in `Program.cs`, clamped to 1 GiB — so no separate
Kestrel configuration is needed. The BFF route
`POST /bff/backend/api/purchases/receipt-imports/upload` is the only one that accepts a large multipart
body and it runs under the stricter `ReceiptUpload` rate limiter.
`ReceiptImports:MaxParallelImports` is declared and shipped in `appsettings.json` but read nowhere:
imports run sequentially per batch.

**Folder import** is deployment-owned. `ReceiptImports:InboxPath` is never accepted from a request; the
feature stays off unless `FolderEnabled=true` and the configured directory exists. The scanner skips
dot-files, `.tmp`, `.part`, the internal `.fullworth` directory and directory reparse points, ignores
files newer than `FolderStableAgeSeconds` (10) so a file still being copied is not imported, and only
reports relative paths, counts and byte totals — the host/NAS root never appears in a JSON response.
Read-only mounts are the right choice: FullWorth never deletes or renames a source file.

**Paperless** access is read-only. The API token is stored through `FieldCipher` and never returned.
Pagination is pinned to the configured server: Paperless commonly advertises `next` links using its own
internal `PAPERLESS_URL`, so `RebaseToConfiguredServer` keeps only the path and query and rebuilds the
URL against the configured base. LAN and private URLs are supported on purpose for self-hosted
installations. Each document becomes one logical receipt. Changing the base URL disables auto-import.
Requests time out after `PaperlessTimeoutSeconds` (clamped 5–300).

**Duplicate handling** is layered:

1. Source identity — Paperless document id per server authority, browser SHA-256 (`<batchId>:<sha>`), or
   folder content fingerprint. A folder file already imported successfully is skipped; when a scan finds
   nothing new the duplicates are still reported so a manual re-scan gives an explicit answer.
2. Content SHA-256 against receipts already visible to the user in this space
   (`ReceiptScanQueueService.VisibleDuplicateAsync`), which also rejects the same file selected twice in
   one batch. Items rejected this way become `skipped_duplicate`.
3. The existing semantic receipt duplicate review after extraction. Uncertain duplicates stay
   reviewable and are never deleted automatically.

**Persisted.** `ReceiptImportBatches`, `ReceiptImportItems`, `PaperlessReceiptConnections` (migration
`20260901211500_BulkReceiptImports`) and `PaperlessImportPresets` (`20260906213000`). Purchases,
`PurchaseDocuments` and `ReceiptScanJobs` are created by the receipt queue, not by these tables. The
migration's down path drops only the three bulk-import tables and leaves everything the imports
produced intact.

Note that `ReceiptExtraction:Provider` defaults to `none`, whose extractor extracts nothing. With the
shipped default an imported receipt is stored and queued but arrives in review empty; set the provider
to `tesseract` to get local OCR.

## Amazon order sync

`/api/purchases/amazon` (`AmazonIntegrationEndpoints.cs`, `AmazonOrderSyncService.cs`):
`GET /status`, `POST /connect/start`, `POST /connect/{challengeId}/complete`, `DELETE /connection`,
`POST /sync`. Amazon exposes no buyer order-history API, so this connector drives the user's own
account with a pinned Playwright/Chromium runtime — `Microsoft.Playwright 1.62.0` against the
`mcr.microsoft.com/playwright/dotnet:v1.62.0-noble` base image, so no browser is downloaded at startup.

**Flow.** The user enters e-mail and password in *Käufe → Amazon verbinden*; if Amazon asks for an OTP
or device approval, that is completed in a second request. Password and OTP are used only for the
active login request and never persisted. What is stored is Playwright's browser storage state,
encrypted with `Security:DataEncryptionKey` through `FieldCipher`. The first successful connection syncs
90 days (`InitialHistoryDays`); the dialog also offers one year and *Alle*. For *Alle*,
`DiscoverAvailableYearsAsync` reads the years Amazon actually offers in the order-history filter and
falls back to a floor of 1995 only when that filter cannot be read. `AmazonSyncWorker` wakes hourly and
re-syncs every connection whose last sync is older than `SyncIntervalHours` (24, clamped 1–168), each
time fetching `InitialHistoryDays` of history; it does nothing when `AmazonIntegration:Enabled` is
false, and a manual sync is always available. Defaults live in `AmazonIntegration` in
`src/FullWorth.Backend/appsettings.json`.

CAPTCHAs are never solved or bypassed. A CAPTCHA or an expired session moves the connection to
`requires_reauth` and the user reconnects. Navigation stays on `https://www.amazon.de` /
`*.amazon.de`; a foreign order-detail href is not followed. A parser failure stops the sync instead of
silently dropping an order. Credentials, OTPs and decrypted storage state are never logged.
Disconnecting deletes the stored browser session and keeps the already imported purchases.

**Mapping.** One Amazon order becomes one `Purchase` with `Source='amazon'` and
`ExternalOrderId` = the Amazon order number; the unique index
`(FullWorthSpaceId, Source, ExternalOrderId)` is the duplicate protection, so a re-sync updates instead
of duplicating. `TotalAmount` and `Currency` are taken from the order exactly as Amazon states them.
Line items become `PurchaseItem` rows carrying name, ASIN, quantity, unit price and line total when
Amazon exposes them; they pass through the normal item categorization rules and stay editable. Editing
an imported item preserves ASIN, brand, SKU, unit price and notes, and a later sync preserves manual
categories and reviewed data even if Amazon temporarily shows less. `AmazonPurchaseFinancials` refuses
to overwrite a confirmed order or a manual discount edit with a freshly parsed interpretation, and only
makes Amazon's subtotal authoritative when subtotal − discounts = total holds within 0.01.

Gift-card and account-balance detection is deliberately conservative: only explicit payment labels
(`Geschenkgutschein-Guthaben`, `Geschenkgutscheinguthaben`, `Amazon-Guthaben`, `Gift Card balance`,
`Gift-card balance`, `Gift card applied`) count. A product whose *name* merely contains
`Geschenkgutschein` is not a payment. The user can correct the non-bank amount by hand and that
correction survives later syncs.

**Bank reconciliation** is many-to-many: one order can be charged in several shipments and one bank
transaction can cover several orders. `PurchasePaymentLinks` is the canonical allocation table — one row
per (purchase, transaction) pair, unique, holding the allocated `Amount` and `Currency`, `LinkSource`
and `Confidence`. `Purchase.TransactionId` remains only as the legacy single-payment link and is
mirrored when exactly one payment is linked. The Amazon-era `PurchaseTransactionLinks` table no longer
exists: `20260830151000_UnifyPurchasePaymentLinks` migrated its allocations into `PurchasePaymentLinks`,
made the pair unique and dropped it.

`AmazonPurchaseMatchingService` does the automatic matching:

- owned negative transactions from 3 days before to 21 days after the order date, Amazon/AMZN
  counterparties preferred, using only the still-unallocated part of each transaction;
- exact combinations of up to 5 charges out of the best 12 candidates, matched to
  order total − gift/account balance within 0.01 in the purchase currency;
- for delayed shipments and pre-orders a second pass out to 365 days, which accepts only one unique
  exact Amazon charge and otherwise leaves the choice to the user;
- afterwards the reverse case: one larger Amazon charge that exactly equals the remaining amounts of
  2–5 imported orders (out of the best 14) is split automatically, but only when exactly one valid
  combination exists — ambiguity stays unlinked for manual review.

An order is `confirmed` only when allocated bank payments plus the Amazon gift/account balance match the
order total within 0.01; otherwise it stays in `review`.

The manual picker (`GET|POST|DELETE /api/purchases/{id}/amazon-payment-links`,
`PUT /api/purchases/{id}/amazon-nonbank-payment`) offers the same 365-day window, shows both the full
bank amount and the amount still available after other allocations, and lets the user choose the
allocated amount. The backend revalidates ownership, currency, date, available transaction amount and
remaining order amount before saving.

Refunds are matched separately by currency, exact amount and date, preferring Amazon counterparties,
with a manual candidate picker (`/api/purchases/{id}/amazon-refunds/{refundId}/...`) when automatic
matching is ambiguous. A refund transaction belongs to one refund and cannot be reused as a purchase
payment. Support tables `AmazonConnections`, `AmazonOrderMetadata` (external status plus detected and
manual non-bank payment amount) and `PurchaseRefunds` are reached through `AmazonSqlStore` rather than
the EF model, keeping the Purchase model and migration snapshot untouched.

Because the connector depends on Amazon's buyer website, the selector/parser tests
(`tests/FullWorth.Backend.Tests/Purchases/AmazonIntegrationTests.cs`,
`AmazonDiscountParserTests.cs`) must be kept — Amazon changing its HTML is a maintenance event, not a
bug report.

## Shared receipt (PWA share target)

The manifest registers `POST /share/receipt` as a share target. The shared files are parked in a
short-lived per-user inbox (at most 24 files, 20 MB each, 60 MB total) and the confirmation page posts
`/share/receipt/{token}/import`, which forwards them to `api/purchases/receipt-scan/jobs` in the chosen
space with that space's base currency, starts the job and redirects to `/purchases`. Nothing is imported
without the explicit confirmation click.

## Payslip extraction

`POST /api/compensation/payslips/extract` and `/extract-batch` (max 40 files) accept a PDF or an image
(JPG, PNG, WebP, TIFF, BMP) up to 12 MB, run `pdftoppm` on the first page and `tesseract -l deu+eng`,
and return structured fields. The uploaded file is never persisted and the work directory is deleted
afterwards. Optional Codex structuring runs over the OCR text only and falls back to the deterministic
regex parser whenever it is disabled, times out or returns anything invalid, so a payslip is never
partially interpreted. Persisting the result is a separate, explicit `POST /api/compensation/payslips`.
See [Compensation analyzer](COMPENSATION_ANALYZER.md).

## Backup validation

`POST /api/import/wealth-backup/validate` accepts a wealth-backup ZIP up to 1 GB and only *checks* it:
manifest present, `format = fullworth-wealth-export-v1`, `schemaVersion = 1`, the manifest's space equal
to the caller's, and every listed asset document present in the archive with a matching SHA-256. It
imports nothing. There is no restore endpoint; restoring is a database-level operation described in
[Operations](OPERATIONS.md).

## What no importer does

- No importer writes an `AssetValuations` row. `import` is an accepted `Method` value, but asset values
  arrive only through manual entry, a valuation provider or the one-off `legacy` backfill in migration
  `20260901203000_AssetValuationHistory`. Assets contribute to net worth from `Assets.CurrentValue` in
  the asset's own `Currency`.
- No importer writes a `SecurityPrices` row. Prices come from the market-data refresh
  (`MarketDataParityModule`), a FinTS depot snapshot (`FinTsInvestmentSnapshotEndpoints`) or manual
  entry.
- No importer writes a `BalanceSnapshot` except the Finanzguru account-link step, and there the value is
  typed by the user. Provider balances come from `IngestionService.InsertBalancesAsync` on the bank-sync
  path, in the currency the provider reported.

## Test coverage

`tests/FullWorth.Backend.Tests/Api/`: `ImportMappingRegressionTests`, `ImportAtomicityRegressionTests`,
`ImportStableIdentityRegressionTests`, `ImportRollbackRegressionTests`,
`ImportTransactionProvenanceGuardTests` (schema guard for the rollback exclusion list),
`InvestmentImportRegressionTests` (Trade Republic buy cancellation, trade/security provenance, rollback
into an existing portfolio, rollback of an import-created portfolio, refusal to roll back an untracked
import, reconciliation cash and holdings, type-count summary) and `InvestmentImportNumberFormatTests`.

`tests/FullWorth.Backend.Tests/Import/`: `FinanzguruImportTests`, `FinanzguruLivePreferenceTests`,
`FinanzguruReconciliationTests`.

`tests/FullWorth.Backend.Tests/Purchases/`: `AmazonIntegrationTests`, `AmazonDiscountParserTests`,
`PurchaseDiscountImportIdempotencyTests`, and `ReceiptImports/` with `ReceiptImportIntegrationTests`,
`FolderReceiptImportIntegrationTests`, `PaperlessReceiptImportIntegrationTests`,
`PaperlessReceiptClientTests` and `ReceiptScanCompatibilityIntegrationTests`.

`tests/FullWorth.Web.Tests/`: `ImportCenterUiBaselineTests`, `ReceiptImportUiBaselineTests`,
`AmazonPurchasesUiBaselineTests` and `Frontend/ImportPageStylesheetGuardTests` (the import pages must
load the full CSS layer chain — loading only `app.css` left every design token undefined).

There is no automated browser or end-to-end test for any import flow.
