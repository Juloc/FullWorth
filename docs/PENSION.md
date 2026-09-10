# Occupational pension (bAV)

Architecture and data model for the pension area. Written before any code, because the wrong model here
would either mix incompatible facts into `RecurringContract`/`Asset` or build a second net-worth system next
to the existing one — the brief rules out both.

Everything below was checked against the code on 2026-09-10.

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

## What genuinely needs its own tables

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

## Money direction — the rule that keeps the numbers honest

A total contribution of 338 € consisting of 169 € employee and 169 € employer is **not** a 338 € outflow.

- The **employee** share is deferred compensation: it reduces net pay and belongs in fixed costs / cashflow.
- The **employer** share is a benefit: it must never appear as a cost, and never as income the user could
  spend.
- The **balance** (Vertragsguthaben) is the asset. It is fed by both shares, which is why the asset value can
  grow faster than the employee's own payments — that is correct and must not be "corrected".

So: `RecurringContract.Amount` = employee share. The employer share lives in `BavContribution` and surfaces
in the employer-benefit view and the pension totals, never in expenses.

## Net worth

- The **current balance** counts as pension assets. `Asset.IncludeInNetWorth` is the per-contract switch the
  brief asks for ("in Gesamtvermögen einbeziehen" vs "nur separat anzeigen").
- A **projection never becomes a value.** Only a snapshot balance is ever written to `Asset.CurrentValue`;
  scenario results are computed on read and carry their assumption with them.
- The wealth overview gets a `pensionAssets` component, a subset of `manualAssets` converted with the same
  rates — the same shape as `realEstateAssets`, so the block and the total can never disagree.
- Free vs tied wealth is a display distinction over the same numbers: pension assets are tied, so the wealth
  page shows both a total and "davon gebunden".

## Snapshots and idempotency

A snapshot is identified by `(BavContractId, EffectiveDate, DocumentSha256)`. Re-uploading the same document
creates nothing. An annual statement for a new year creates a new snapshot and never touches the previous
one — no field of an existing snapshot is ever overwritten.

Existing-contract detection on upload matches, in order: policy number (normalised), then provider + tariff +
employer, then provider + retirement date. A match adds a snapshot; only an unmatched document offers to
create a contract, and the user confirms either way.

## Extraction

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

## Beitragsfrei

`Status = paid_up` means: no new contributions, the contract exists, the balance exists, costs may continue,
the value keeps developing. It is not `terminated` and not "free of charge". Contributions get an end date;
snapshots and costs continue.

## Projections and variant comparison

A projection takes today's balance, the future contributions, a return scenario (3 / 5 / 7 / custom) and the
**known** costs, and reports the guarantee separately. Nothing is presented as guaranteed that is not.

The comparison must not invent an advantage: with identical return and identical costs, 50 € + 288 € equals
338 €. Splitting a contribution across contracts produces no extra compound interest. Differences come from
costs, guarantees and investment concept, and the comparison names which of the three caused the delta.

## Known limits, stated up front

- Tax and social-security effects are taken from a document or a payslip, or shown as an explicitly labelled
  simulation. FullWorth does not compute a personal net effect on its own.
- A managed portfolio is shown as one unit with its known components; the UI never implies that individual
  funds can be changed when the tariff does not allow it.
- An employer is a name, not an entity, until the compensation area has employers of its own.
