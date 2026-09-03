# FullWorth database migrations

`FullWorth.Backend` owns the finance PostgreSQL schema. EF Core migrations in `src/FullWorth.Backend/Migrations` are the schema source of truth.

## Normal startup

On startup `FullWorth.Backend` applies pending EF Core migrations and then runs `FullWorthSeeder` for required application defaults.

A fresh empty PostgreSQL database is therefore created through the checked-in migration chain. Application seeding is separate from migration `Up()` methods.

## Legacy EnsureCreated databases

Pre-migration development databases may have application tables created by `Database.EnsureCreated()` but no `__EFMigrationsHistory` table.

This version does not automatically adopt such a database and does not insert migration-history rows for it. The old schema cannot be treated as migration-managed unless its complete relational schema is proven to match the migration exactly. Failing closed avoids marking a partially different database as current.

For the pre-1.0 transition use this controlled process:

1. Stop writes and back up/export the legacy development data.
2. Provision an empty PostgreSQL database for the migration-enabled version.
3. Start `FullWorth.Backend` so EF Core creates the schema and migration history and `FullWorthSeeder` creates missing defaults.
4. Import required data into the migration-managed database through an explicit controlled data-only/import process that preserves identifiers and validates unique constraints and relationships.
5. Verify record counts, important relationships, bank/account identifiers and application health before discarding the legacy database or backup.

Do not point the migration-enabled application at an unverified `EnsureCreated` database and do not manually add rows to `__EFMigrationsHistory` as a shortcut.

## Wave B: MultiUserAndFullWorthSpaces

`20260811214402_MultiUserAndFullWorthSpaces` is the single coordinated Wave-B migration applied after `InitialFinanceSchema`.

It adds:

- `Users`
- `FullWorthSpaces`
- `FullWorthSpaceMembers`
- `AccountOwners`
- required direct `FullWorthSpaceId` scope on `BankConnections`, `Accounts`, `Categories`, `CategorizationRules`, `Contracts`, `Budgets`, `Assets`, `Liabilities`, `NetWorthSnapshots` and `Purchases`
- per-FullWorth-Space category-key uniqueness
- account reconciliation uniqueness scoped by FullWorth Space
- owner/member and owner/viewer database checks

`BalanceSnapshots`, `Transactions` and `PurchaseItems` intentionally do not receive duplicate `FullWorthSpaceId` columns. Their scope is derived from their parent account or purchase.

### Existing migration-managed B0 databases

The migration uses one deterministic compatibility FullWorth Space for all existing pre-auth finance data:

- Id: `7b21b1a4-0b7b-4ae1-93d0-b8d1f859e8a1`
- Name: `Default`
- Base currency: `EUR`

New direct scope columns are added nullable for the transition, existing rows are backfilled, and only then are the columns changed to `NOT NULL`. Existing accounts first inherit the FullWorth Space of their persisted `BankConnection`; any standalone pre-auth top-level rows use the same deterministic compatibility space.

The migration does not infer a person from legacy data. It creates no fake `FullWorthUser`, no fake `FullWorthSpaceMember` and no fake `AccountOwner`. Existing identifiers and references, including category parent/references, account history, transactions, purchases and purchase items, are preserved.

Legacy net-worth snapshots receive the compatibility `FullWorthSpaceId` but no invented user audience. Their nullable `UserId` remains `NULL`. New runtime snapshots are produced per real FullWorth-Space member and only from accounts visible through that member's `AccountOwner` rows.

Real authenticated user creation and attaching a real owner/member to the compatibility FullWorth Space belong to the later authentication/setup phase.

### Fresh databases before Wave C

A fresh database receives the deterministic `Default` compatibility FullWorth Space from the migration and `FullWorthSeeder` creates the 19 default categories for it. No user, membership or account-owner identity is fabricated. The same seeder also inserts missing defaults independently for every later FullWorth Space and never overwrites an existing category row.

This compatibility space exists so pre-auth transitional endpoints and deterministic seeding have one explicit scope until trusted Web identity/setup exists. It is not authentication and it is not permission evidence.

### Relationship enforcement

The database directly enforces normal foreign keys, required scope columns, safe delete behavior, category/account uniqueness boundaries and role/ownership checks.

Same-space relationships that would otherwise require redundant composite-key schema are validated in the Store/Service or ingestion boundary:

- `FinanceAccount.BankConnectionId`: ingestion copies scope from the persisted `BankConnection` and account reconciliation is scoped by that connection's FullWorth Space.
- `FinanceCategory.ParentId`: `CategoryStore` requires the parent in the same FullWorth Space.
- `FinanceTransaction.CategoryId`: `TransactionStore` derives scope through the parent account and accepts only a category from that FullWorth Space.
- `CategorizationRule.CategoryId`: `CategoryStore` requires the category in the same FullWorth Space.
- `RecurringContract.CategoryId` and `AccountId`: `ContractStore` validates both references against the contract FullWorth Space.
- `Budget.CategoryId`: `BudgetStore` validates the category FullWorth Space.
- `Purchase.TransactionId`: `PurchaseStore` derives transaction scope through its account and requires it to match the purchase FullWorth Space.
- `PurchaseItem.CategoryId`: item replacement validates the category against the parent purchase FullWorth Space.

Account visibility is additionally an application-layer invariant: a current `FullWorthSpaceMember` row and an `AccountOwner` row are both required. FullWorth-Space membership alone does not expose a private account.
