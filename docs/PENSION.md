# Occupational pension (bAV)

Architecture and data model for the pension area. Written before any code, because the wrong model here
would either mix incompatible facts into `RecurringContract`/`Asset` or build a second net-worth system next
to the existing one — the brief rules out both.

Everything below was checked against the code on 2026-09-10.

## Build status

The work is cut into three steps. **Steps 1 and 2 are built** (migrations
`20260910233000_OccupationalPension` and `20260911010000_PensionDocumentExtraction`); step 3 is open.

| Step | Contents | State |
| --- | --- | --- |
| 1 | Domain model, migration, REST API, the manual entry flow, tests | **DONE** |
| 2 | Document upload, extraction, review, snapshot commit | **DONE** |
| 3 | Wealth/salary/dashboard integration, projections and variant comparison | OPEN |

What exists after step 1:

- `src/FullWorth.Backend/Modules/Pension/` — entities (`PensionModels.cs`), EF mapping
  (`PensionModelConfiguration.cs`), identity/normalisation (`PensionIdentity.cs`), DTOs
  (`PensionDtos.cs`), the store (`PensionStore.cs`) and the endpoints (`PensionEndpoints.cs`).
- Six tables: `BavContracts`, `BavSnapshots`, `BavContributions`, `BavInvestmentAllocations`,
  `BavCosts`, `BavDocuments`. `BavDocuments` is created empty for step 2 and has no upload path yet.
- `/api/pension/*`: `GET overview`, `GET|POST contracts`, `POST contracts/match`,
  `GET|PUT|DELETE contracts/{id}`, `GET|POST contracts/{id}/snapshots`,
  `POST contracts/{id}/{contributions,costs,allocations}`. Reads need space membership, writes the
  owner role; the ordering is not-found → forbidden → conflict.
- `wwwroot/features/pension.js` + `wwwroot/styles/features/pension.css`, view `pension`, routes
  `/pension`, `/pension/vertraege`, `/pension/verlauf`. Übersicht / Verträge / Verlauf with the full
  manual entry flow. **No simulation tab yet — that is step 3.**
- Tests: `tests/FullWorth.Backend.Tests/Pension/` (18) and
  `tests/FullWorth.Web.Tests/PensionUxBaselineTests.cs` (3).

Known limitations after step 1, all deliberate:

- The **Verlauf** tab lists the current value per contract and links into the contract, where the full
  dated history is. A cross-contract dated timeline needs the wealth integration of step 3.
- Contributions, costs and fund allocations are **append-only** through the API. Nothing edits or
  deletes a single one of them yet, which is the safe direction: a wrong row is superseded by a newer
  dated row rather than rewritten. A correction path (supersede, not overwrite) is step 3 work.
- `BavContract.RecurringContractId` is stored and returned but nothing writes it yet: creating the
  fixed-costs contract for the employee share is part of the cashflow integration in step 3.
- The overview converts with the existing FX table and reports a missing rate as incomplete. It does
  not yet feed `pensionAssets` into the wealth overview — step 3.
- No locale keys were added (`nav.pension`, `pages.pension`); the module carries its own copy, and the
  shell falls back to the nav button's own label. Adding the two keys later is cosmetic.

## What already exists and is reused

| Need | Existing mechanism | Decision |
| --- | --- | --- |
| A value that counts in net worth | `Asset` (`Kind`, `CurrentValue`, `Currency`, `ValuedAt`, `IncludeInNetWorth`) — and `AssetKinds.InsurancePension` is **already defined** | Reused. One `Asset` per pension contract. |
| A value **history** that never overwrites the past | `AssetValuations` — date, amount, currency, method, low/high estimate, confidence, provider key, external reference, `IsCurrent`/`IsAccepted`, created-by | Reused as the value dimension of a snapshot. No second valuation table. |
| A recurring payment in fixed costs and cashflow | `RecurringContract` (amount, cycle, next due, account, merge/continuity detection) | Reused for the **employee** share only — see "Money direction". |
| Monthly bAV amounts from real payroll | `PayslipExtractionResult` already carries `BavEmployee` and `BavEmployer` with a confidence and detected labels | Reused as the source of the net effect, so nothing is invented for tax. |
| PDF → text → OCR → structured → review → store | The payslip pipeline: local OCR (`pdftoppm` + Tesseract deu+eng), a deterministic regex parser, and an **optional** Codex structuring pass behind a strict JSON schema that falls back to the parser on any failure | Reused as the pattern, with two deliberate differences (below). |
| Idempotent document import with a review step | The import jobs pattern (`ImportJobs.FileSha256`, candidate rows with a validation status, review, then commit) | Reused for pension documents. |
| Grouping a subset of assets into its own display block | The `realEstateAssets` subset the wealth overview returns for the allocation chart | Same shape for the pension block. |

Two places where the payslip pipeline does **not** fit as-is:

- it OCRs the **first page only**; an annual pension statement is multi-page, so the pension extractor pages
  through the document;
- it **never persists** the uploaded file; pension documents must be stored (the brief lists documents as a
  contract field), so they are stored encrypted and are never sent anywhere by the deterministic path.

## What genuinely needs its own tables — DONE (step 1)

A pension contract carries facts that have no home in `RecurringContract` or `Asset`, and squeezing them in
would be exactly the "fachlich falsche Vermischung" the brief forbids: implementation route
(Direktversicherung / Unterstützungskasse / Pensionskasse / Pensionsfonds / Direktzusage), policy holder vs
insured person, retirement date, guarantee quota and guaranteed annuity factor, the employer/employee split,
the security-assets vs fund-assets ratio, structured costs, and a fund allocation.

```
BavContract            one contract (provider, tariff, policy number, route, employer, dates, status)
  ├─ BavSnapshot       one dated state (balance, guarantee, annuity, security/fund split, source document)
  ├─ BavContribution   one dated contribution arrangement (total, employee, employer, extra employer)
  ├─ BavInvestmentAllocation  one fund/ETF weight at a point in time (name, ISIN, weight, cost, class)
  ├─ BavCost           one structured cost item (kind, fixed/percent, incurred/future, estimated flag)
  └─ BavDocument       one uploaded document (hash, kind, pages, extraction confidence, stored blob)
```

Links out, so nothing is duplicated:

- `BavContract.AssetId` → the `Asset` that carries the current balance into net worth.
- `BavContract.RecurringContractId` → the `RecurringContract` that carries the employee payment into fixed
  costs. Null for a purely employer-financed contract (a Unterstützungskasse typically has no employee
  payment at all).
- `BavContract.EmployerName` ties the employer benefit view to the compensation area without a hard FK,
  because an employer is not an entity in FullWorth today.

A Unterstützungskasse is its own `BavContract` row with its own route. It is never folded into a
Direktversicherung, even when both belong to the same employer and are reported in one document.

Two things the implementation added on top of this sketch, both because the brief demands them:

- **The policy number is stored encrypted.** It is personal data, so `BavContract` carries
  `PolicyNumberEncrypted` (`FieldCipher`, AES-256-GCM), `PolicyNumberLookup` (the keyed blind index the
  identity match actually runs on, built the same way `AccountIdentifierLookup` builds an IBAN's) and
  `PolicyNumberLast4` for display. The API never returns the number, only `hasPolicyNumber` plus the
  last four characters, and nothing writes it to a log line. `PensionIdentity` also stores a normalised
  `ProviderKey` so "Allianz Lebensversicherungs-AG" and "Allianz Lebensversicherung AG" are one
  provider; the normaliser knows German legal forms, never a provider.
- **The tax and social-insurance effect lives on `BavContribution`**, not in a seventh table:
  `TaxSavingAmount`, `SocialSecuritySavingAmount`, `NetEffortAmount` plus a mandatory `TaxEffectSource`
  (`document` / `payslip` / `simulation`) and a free-text reference. The store refuses an amount without
  a source and `CK_BavContributions_TaxEffectSource` refuses it at the database level too, so FullWorth
  cannot invent a tax effect even through a writer that bypasses the store.

## Money direction — the rule that keeps the numbers honest — DONE (step 1)

A total contribution of 338 € consisting of 169 € employee and 169 € employer is **not** a 338 € outflow.

- The **employee** share is deferred compensation: it reduces net pay and belongs in fixed costs / cashflow.
- The **employer** share is a benefit: it must never appear as a cost, and never as income the user could
  spend.
- The **balance** (Vertragsguthaben) is the asset. It is fed by both shares, which is why the asset value can
  grow faster than the employee's own payments — that is correct and must not be "corrected".

So: `RecurringContract.Amount` = employee share. The employer share lives in `BavContribution` and surfaces
in the employer-benefit view and the pension totals, never in expenses.

## Net worth — PARTLY DONE (the asset link is built; the wealth block is step 3)

- The **current balance** counts as pension assets. `Asset.IncludeInNetWorth` is the per-contract switch the
  brief asks for ("in Gesamtvermögen einbeziehen" vs "nur separat anzeigen"). **Done:** creating a contract
  creates exactly one `Asset` of kind `insurance_pension`, `BavContract.AssetId` points at it, and the
  contract has no second flag of its own — the API reads `IncludeInNetWorth` off the asset.
- A **projection never becomes a value.** Only a snapshot balance is ever written to `Asset.CurrentValue`;
  scenario results are computed on read and carry their assumption with them. **Done:** only
  `BavSnapshot.Balance` of the newest-dated snapshot reaches the asset, in the snapshot's own currency and
  as of the snapshot's own date, so the existing `AssetValuations` trigger records the history. Guaranteed
  and projected figures sit in their own columns and never touch the asset. Entering last year's statement
  after this year's does not move the value backwards, because "current" is the newest effective date and
  not the newest insert.
- The wealth overview gets a `pensionAssets` component, a subset of `manualAssets` converted with the same
  rates — the same shape as `realEstateAssets`, so the block and the total can never disagree. **Step 3.**
  `GET /api/pension/overview` already produces the per-currency total with the same
  incomplete/missing-currency semantics, so the wealth block has something correct to consume.
- Free vs tied wealth is a display distinction over the same numbers: pension assets are tied, so the wealth
  page shows both a total and "davon gebunden". **Step 3.**

## Snapshots and idempotency — DONE (step 1)

Implemented exactly as written, plus two details the sketch left open:

- the unique index is created with `NULLS NOT DISTINCT`, so a second **hand-entered** snapshot for a date
  that already has one is a conflict rather than an indistinguishable duplicate. Both the store and the
  index refuse it; the API answers 409 with `existingSnapshotId`;
- creating a contract whose policy number already exists at that provider answers 409 with
  `existingContractId`, so an importer or the UI is pointed at the contract the statement belongs to.
  `POST /api/pension/contracts/match` answers the same question before anything is written and names
  which of the three rules fired (`policy_number`, `provider_tariff_employer`,
  `provider_retirement_date`), because a weaker match has to be confirmed by a person. It is a POST so a
  policy number never enters a URL or an access log.

A snapshot is identified by `(BavContractId, EffectiveDate, DocumentSha256)`. Re-uploading the same document
creates nothing. An annual statement for a new year creates a new snapshot and never touches the previous
one — no field of an existing snapshot is ever overwritten.

Existing-contract detection on upload matches, in order: policy number (normalised), then provider + tariff +
employer, then provider + retirement date. A match adds a snapshot; only an unmatched document offers to
create a contract, and the user confirms either way.

## Extraction — DONE (step 2)

Built as sketched. The pipeline and its four seams are declared in
`Modules/Pension/PensionDocumentContracts.cs`, so each half could be written against a fixed shape:

| Seam | Implementation | Notes |
| --- | --- | --- |
| `IBavDocumentBlobStore` | `PensionDocumentBlobStore` | AES-256-GCM per blob under `PensionStorage:RootPath` (`/data/pension`). The key is **HKDF-derived** from the data key under its own label, not the data key itself: blobs sit in a directory a backup job copies wholesale, and a leaked blob key must not be a field key. `yyyy/MM/{id:N}.bin` — a directory listing reveals neither space nor file type. |
| `IBavDocumentTextSource` | `PensionDocumentTextSource` | `pdftotext -layout` first, OCR only where there is no text layer, **all pages** (capped at 30) — the payslip pipeline reads page one only. Failures surface as a category, never as tool output. |
| `IBavDocumentParser` | `PensionStatementParser` | Deterministic, pure, locale-independent: amounts through `ImportNumber`, dates through `ImportDate`, plus a German month table. Money/percent/ISIN spans are blanked before the money scan, so `01.01.2026` is not read as 1.01. |
| `IBavDocumentAiStructurer` | `PensionDocumentCodexStructurer` | Fills only what the parser left empty, never overwrites it, and is a no-op without a bridge. Its confidence is capped below any matched label, and a `0` from the model counts as "not stated". |

`PensionDocumentStore` + `PensionDocumentEndpoints` add
`POST|GET /api/pension/documents`, `GET|DELETE /documents/{id}`, `GET /documents/{id}/content`,
`PUT /documents/{id}/review` and `POST /documents/{id}/commit`. The commit runs in one transaction, so a
partial failure leaves a reviewable document rather than a half-written contract; a snapshot date the
contract already holds is **skipped and named**, not an error that loses the rest of the document.

`wwwroot/features/pension-documents.js` is the Dokumente tab and the review screen. It is a **page, not
a dialog** — 56 fields in a dialog would have been the worst offender in the app (docs/UI_AUDIT.md) — and
the only dialog in the feature is the commit confirmation: one sentence, two buttons, no inputs.

Three decisions that are worth knowing before touching it:

- **The policy number never reaches the browser.** The review draft is an API response, and an API
  response is the one place a policy number leaks into a cache, a screenshot or a log — the same reason
  `BavContractView` exposes only the last four characters. The consequence is handled rather than
  accepted: a null coming back means *unchanged*, not *delete*, so the store puts the stored number back
  before it commits. Without that, a reviewed draft would create a contract with no policy number even
  though the document stated one, and the next statement would no longer match it.
- **`applied`/`skipped` are machine tokens** (`BavCommitTokens`), bare or `token:detail`. The API has no
  business holding German and the review screen cannot translate an English sentence.
- ~~**A fund allocation cannot name its document.**~~ Closed in step 3 by migration
  `20260911120000_PensionAllocationDocument`: `BavInvestmentAllocations` has a `BavDocumentId` with
  `ON DELETE SET NULL`, matching `BavCosts`, so deleting a document does not delete values a person
  reviewed and accepted. It matters more here than for the other three rows a commit writes: a
  statement lists the fund split for one point in time, so two statements for neighbouring dates
  produce positions that are otherwise indistinguishable - there was no way to tell which reading a
  position came from, or to undo one document's positions without touching the other's.

What step 1 had left ready and step 2 used: the `BavDocuments` table with its per-space unique
`Sha256`, the nullable `BavContractId` (a document is stored before it is matched),
`ExtractionStatus`/`ExtractionConfidence`/`ExtractionSource`/`PageCount`/`StoragePath`, the
`POST /api/pension/contracts/match` detection endpoint, and a `documentSha256` on every snapshot so a
re-read of the same file creates nothing.

```
upload → store document (encrypted) → text layer, OCR only if there is none
       → deterministic parser (labels, amounts, dates, percentages)
       → optional Codex structuring for the fields the parser could not fill
       → review screen with every field editable, each showing value, source page and confidence
       → commit: contract (new or matched) + snapshot + contributions + allocation + costs
```

Rules that are not negotiable:

- no extracted value is stored without the review step;
- an estimated cost is stored with its estimate flag and displayed as an estimate, never as a contract value;
- AI runs only through the existing Codex bridge, only when the deterministic parser is short, and never on
  self-hosted installations that have no bridge — the manual flow and the deterministic parser are the
  supported path without AI;
- no document content, policy number or personal data is written to a log line.

## Beitragsfrei — DONE (step 1)

`Status = paid_up` means: no new contributions, the contract exists, the balance exists, costs may continue,
the value keeps developing. It is not `terminated` and not "free of charge". Contributions get an end date;
snapshots and costs continue.

How that is enforced: `paid_up` is its own value in `CK_BavContracts_Status`, listed next to `active`;
`BavContractStatuses.HoldsCapital` groups `active`, `paid_up` and `in_payout`, and the API returns
`holdsCapital` and `isPaidUp` so no caller has to guess. A contribution row ends with
`ValidUntil` + `EndReason = paid_up`, and every `BavCost` carries `ContinuesWhenPaidUp` (default true),
which is what makes "beitragsfrei ≠ cost-free" a stored fact rather than a hope. On screen the status
badge is neutral — only `terminated` is dimmed — and the detail says in words that contract, balance
and costs continue.

## Projections and variant comparison — OPEN (step 3), storage DONE

A projection takes today's balance, the future contributions, a return scenario (3 / 5 / 7 / custom) and the
**known** costs, and reports the guarantee separately. Nothing is presented as guaranteed that is not.

The comparison must not invent an advantage: with identical return and identical costs, 50 € + 288 € equals
338 €. Splitting a contribution across contracts produces no extra compound interest. Differences come from
costs, guarantees and investment concept, and the comparison names which of the three caused the delta.

The **distinction** a projection needs is already storable and enforced, so step 3 only has to compute:
`BavSnapshot` keeps `GuaranteedCapitalAtRetirement` / `GuaranteedMonthlyAnnuity` apart from
`ProjectedCapitalAtRetirement` / `ProjectedMonthlyAnnuity`, and a projected figure cannot exist without
`ProjectionBasis` (`document_guaranteed` / `document_forecast` / `simulation`) and
`ProjectionReturnPercent` — the store and `CK_BavSnapshots_Projection` both refuse it, because a bare
projection is indistinguishable from a guarantee. The API adds `projectionIsSimulation`, and the
Übersicht renders the projection in its own labelled block, never next to the balance.

## Known limits, stated up front

- Tax and social-security effects are taken from a document or a payslip, or shown as an explicitly labelled
  simulation. FullWorth does not compute a personal net effect on its own.
- A managed portfolio is shown as one unit with its known components; the UI never implies that individual
  funds can be changed when the tariff does not allow it.
- An employer is a name, not an entity, until the compensation area has employers of its own.
