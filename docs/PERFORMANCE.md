# FullWorth — Performance & Load Testing

Guidance for validating that FullWorth stays responsive with a realistic personal-finance dataset.
These checks are **safe and local** — they run against a throwaway Postgres, never live banks or
providers. Nothing here optimizes prematurely; it exists to catch a regression or a missing index
with evidence before it reaches a real deployment.

## Representative dataset
- a few users / FullWorth Spaces with shared accounts,
- dozens of accounts,
- **100k+ transactions**, many with normalized counterparties,
- purchase items, category rules and net-worth snapshots.

## What to measure
Transaction search / filter / sort, dashboard, category & merchant analytics, JSON/CSV export, and
batch ingestion into the local backend. Record wall-clock latency at p50/p95 for each.

## Running the opt-in harness
The load harness (`tests/FullWorth.Backend.Tests/Performance/**`) is **skipped by default** so CI stays
fast; enable it explicitly against a real Postgres:

```bash
# Docker Postgres 18 (see docs and reference-local-testing)
export FULLWORTH_TEST_POSTGRES="Host=localhost;Port=5432;Username=fullworth_test;Password=fullworth_test_password"
export FULLWORTH_PERF=1          # opt in
export FULLWORTH_PERF_TX=100000  # dataset size (default 100000)
dotnet test tests/FullWorth.Backend.Tests/FullWorth.Backend.Tests.csproj -c Release --filter "FullyQualifiedName~Performance"
```

`TransactionQueryPerformanceTests` seeds `FULLWORTH_PERF_TX` transactions and asserts a filtered +
sorted top-200 query returns in under 2s. Extend it with analytics/export timings as needed; keep
each assertion a generous ceiling (regression detector), not a micro-benchmark.

## Indexing baseline
The schema already carries the indexes these queries rely on — verify with `EXPLAIN (ANALYZE)` under
load rather than adding indexes speculatively:
- `Transactions`: `BookingDate`, `AccountId`, `TransferGroupId` (filter/sort + account scoping).
- Account/space scoping: `Accounts.FullWorthSpaceId`, `AccountOwners (AccountId, UserId)`,
  `FullWorthSpaceMembers (FullWorthSpaceId, UserId)`.
- Feature tables: `Budgets (IsActive, CategoryId)` + `FullWorthSpaceId`, `NetWorthSnapshots (Date,
  Currency)`, `PriceChangeSuggestions (ContractId)`, `PushDevices (FinanceUserId, Endpoint)`.

## Method
1. Seed the dataset, `ANALYZE` the database.
2. Time each operation cold, then warm; capture p50/p95.
3. For anything over target, capture the `EXPLAIN (ANALYZE)` plan and identify the missing index or
   the N+1 query — **with evidence** — before changing anything.
4. Never weaken authorization scoping (the per-space/owner filters) for speed.
