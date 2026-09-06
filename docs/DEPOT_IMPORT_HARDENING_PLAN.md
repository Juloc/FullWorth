# Depot Import Hardening Plan

Status: implementation branch `feat/depot-import-hardening`

## Goal

Make depot imports safe enough to be repeatable and reversible, and make imported portfolio state auditable after the import.

## Scope implemented in this plan

### 1. Import history and exact rollback

- Persist the target portfolio on every completed investment import job.
- Persist exact links from an import job to every trade created by that job.
- Persist exact links from an import job to every security created by that job.
- Expose recent investment-import history for the current FullWorth Space.
- Allow rollback only when FullWorth can prove which rows were created by that import.
- Rollback deletes only linked imported trades.
- Securities created by the import are deleted only when they are no longer referenced.
- A portfolio created by the import is deleted only when it is empty after rollback.
- Legacy imports created before link tracking remain visible but are not offered an unsafe automatic rollback.

### 2. Portfolio reconciliation

- Expose a reconciliation endpoint per portfolio.
- Recalculate holdings from the investment ledger including buys, sells, security transfers, splits and cancellations.
- Calculate a transparent estimated cash balance in portfolio currency.
- Return warnings for negative holdings, negative estimated cash, mixed currencies and uncategorized/other investment events.
- Return reconciliation in the commit result so an import immediately reports its post-import health.

### 3. Transaction semantics and corporate-action basics

- Add a first-class `cancellation` investment transaction type.
- Map Trade Republic `BUY_CANCELLED` to `cancellation`.
- Include cancellations in ledger validation and position/cost-basis calculation without treating them as taxable sells.
- Keep `REDEMPTION` as sell because it closes a position for proceeds.
- Keep security transfers and splits as non-cash corporate actions.
- Extend investment management validation to accept the same canonical transaction types.

Provider-specific mergers, spin-offs and ISIN changes need richer source data than the current CSV carries and are intentionally not guessed.

### 4. Review UX

- Show transaction-type counts before commit.
- Show post-import reconciliation warnings.
- Show recent depot import history in the import center.
- Offer a rollback action only for imports with exact provenance links.

### 5. Regression coverage

Cover:

- Trade Republic buy cancellation semantics.
- exact import trade/security provenance.
- rollback of an import into an existing portfolio.
- rollback of an import-created portfolio.
- no rollback of legacy/untracked imports.
- reconciliation cash and holdings.
- type-count summary and UI contract.
