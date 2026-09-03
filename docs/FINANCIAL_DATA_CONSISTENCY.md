# Financial data consistency

## Product invariant

A successful financial source-data mutation must leave every dependent read model consistent before the request is considered complete.

This applies equally to:

- live bank synchronization;
- Finanzguru/CSV-style historical imports;
- transaction create/update/delete and bank pending-to-booked reconciliation;
- balance changes;
- account activation, net-worth inclusion, currency and ownership changes;
- FullWorth Space membership changes;
- assets and liabilities.

## Source of truth vs derived data

Transactions, balances, accounts, ownership, assets and liabilities are source data. Analytics, budgets, category/merchant reports and forecasts query source data directly and must not maintain independent stale copies.

`NetWorthSnapshot` is materialized derived data and is refreshed by the central EF Core consistency pipeline in `Data/FinancialDataConsistency.cs`.

## Transaction boundary

Bank sync and historical import perform several `SaveChanges` calls inside one explicit database transaction. Invalidation is therefore accumulated for the whole `FullWorthDbContext` and processed only after the transaction commits. Rolled-back or failed writes never refresh derived data.

For ordinary writes without an explicit transaction, refresh runs after `SaveChanges` succeeds.

## Historical net worth

The newest trusted account balance is the anchor. Booked transactions are applied backwards by booking/value date to reconstruct daily account balances through the affected historical range.

Rules:

- pending transactions are excluded;
- only transactions in the account's native currency are used to back-cast that account balance;
- current asset/liability values are never invented for historical dates;
- known historical asset/liability snapshot components are retained;
- a startup repair pass rebuilds existing history after deployment so old imports do not need to be repeated.

If an imported archive account has no live/current balance anchor, an absolute historical net-worth value cannot be inferred safely from transactions alone. Such history becomes reconstructable once the archive is reconciled to a live account with a trusted balance.

## Implementation rule

New persisted financial source entities or new materialized financial read models must be added to this consistency mechanism as part of the same feature. Do not add page-specific refresh hacks.
