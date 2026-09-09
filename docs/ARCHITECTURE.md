# FullWorth architecture

.NET 10 backend, vanilla-JS frontend, PostgreSQL 18. Everything ships as one container.

Related documents: [Security architecture](SECURITY_ARCHITECTURE.md) (trust boundaries, auth),
[Frontend architecture](FRONTEND_ARCHITECTURE.md) (the `wwwroot` contract),
[Migrations](MIGRATIONS.md) (schema history and the Wave-B space migration),
[Cloud](CLOUD.md) (the FullWorth Cloud client), [Operations](OPERATIONS.md), [Release](RELEASE.md).

## One process, three modules

`ghcr.io/juloc/fullworth` runs `FullWorth.Web.dll` with `FullWorthHost__Unified=true`. Web,
Backend and Banking are three code modules in one Kestrel process on `:8080` (published to
`127.0.0.1:8098` by default). The split `fullworth-web` / `-backend` / `-banking` images are not
built any more; `release.yml` publishes exactly two images, `fullworth` and `fullworth-codex`.

Composition happens in `src/FullWorth.Web/Program.cs`:

```csharp
var unifiedHost = builder.Configuration.GetValue("FullWorthHost:Unified", false);
...
builder.AddFullWorthBackend(unifiedHost: true);   // Backend/Hosting/BackendApplication.cs
builder.AddFullWorthBanking(unifiedHost: true);   // Banking/Hosting/BankingApplication.cs
```

`FullWorth.Backend` and `FullWorth.Banking` still have their own `Program.cs` and `Dockerfile`, and
the `unifiedHost: false` branches are still compiled. That is a dormant topology, not a supported
deployment.

Consequences of sharing one process:

- Ownership of the pipeline is split by path. `UseFullWorthBackend` wraps its middleware in
  `UseWhen(path starts with /api && not /api/banking || path starts with /internal)`;
  `UseFullWorthBanking` wraps its own in `UseWhen(path starts with /api/banking)`.
- Backend and Banking endpoints are mapped into `app.MapGroup(string.Empty).AllowAnonymous()`. The
  Web host's authenticated fallback policy must not apply to them — their own internal-key and
  user-context middleware stays authoritative.
- `UseHttpsRedirection` is skipped for loopback remote addresses only, so internal module calls stay
  on plain HTTP while external traffic is still redirected.
- The Web host registers `PurchasePaymentAllocationConflictExceptionHandler` before its generic
  handler so the backend's database-enforced 409 semantics survive inside the Web pipeline.
- `/health` answers `{ "status": "ok", "service": "fullworth" }`.
- The image base is `mcr.microsoft.com/playwright/dotnet` plus `tesseract-ocr`, `tesseract-ocr-deu`
  and `poppler-utils`, because the Amazon connector, receipt OCR and PDF import all live in this
  one process now.

## The BFF and its loopback hops

The browser only ever talks to same-origin Web routes. Every internal call is an HTTP request to
`127.0.0.1:8080` — the same socket the request arrived on.

**Browser → Backend.** `/bff/backend/{**path}` (`RequireAuthorization`, `RateLimitPolicies.BrowserApi`).

1. `api/bootstrap*` returns 404. That seam runs on the internal key with no user context and must
   never be reachable from a browser session.
2. `ProxyTargetValidator.TryBuildTarget` composes the path against the client `BaseAddress`
   (`Services:BackendUrl`, `http://127.0.0.1:8080` in Compose) and accepts it only when scheme, host
   and port match exactly, there is no userinfo or fragment, the path contains no `\`, `%5c`, `%2f`
   or `%25`, and the normalized path starts with `/api/`. A rejected path produces zero outbound
   traffic.
3. `BackendUserContextHandler` re-checks the origin, strips every untrusted forwarding header
   (`BackendContextHeaders.UntrustedForwardingHeaders`, including `Authorization` and `Cookie`),
   resolves `AuthUser` → `FinanceUserId`, and only then attaches `X-FullWorth-Internal-Key` and
   `X-FullWorth-User-Id`. A disabled user or an unmapped `FinanceUserId` gets 401 without a request
   leaving the process. Password lockout deliberately does not block this path.
4. `InternalUserContextMiddleware` validates the internal key in fixed time and sets
   `CurrentUserContext`.

`/bff/backend/api/purchases/receipt-imports/upload` is a separate route: it raises
`MaxRequestBodySize` to `ReceiptImports:MaxUploadBytes` (default 512 MiB, clamped to 1 GiB) and uses
`RateLimitPolicies.ReceiptUpload` instead of the generic browser budget.

**Browser → Banking.** `/bff/banking/{**path}`, allowlist `/api/banking/`.
`BankingUserContextHandler` discards all inbound `X-FullWorth-*` and `Psu-*` headers and rebuilds
them from the real ASP.NET request: `X-FullWorth-User-Id` from the session,
`X-FullWorth-Space-Id` from the `fullWorthSpaceId` query value, and `Psu-Ip-Address` /
`Psu-User-Agent` / `Psu-Referer` / `Psu-Accept*` from the actual browser request.
`Psu-Geo-Location` is never sent. `ServiceProxyGuardHandler` then runs the origin check a second
time and attaches `X-FullWorth-Banking-Key` after it.

**Banking → Backend.** `FullWorthBackendClient` (`Backend:BaseUrl`) posts sync results to the
backend's `/internal` surface with `X-FullWorth-Ingest-Key`, checked by the first middleware in
`ConfigureBackendMiddleware`.

**First-run bootstrap.** `FirstRunBootstrapper` calls the backend's internal-key-only bootstrap
endpoint over HTTP. In unified mode that endpoint is served by this process, so it is registered on
`app.Lifetime.ApplicationStarted` — running it before `app.Run()` hits a socket that is not bound
yet and a fresh deployment would come up with no admin login.

Proxy rules that matter:

- All three service clients set `AllowAutoRedirect = false`. A 302 from a service (the Enable
  Banking callback answering `Location: /?bankConnected=…`) has to reach the browser, which resolves
  it against the public origin.
- `ProxyAsync` sends `Accept: application/json` and does not forward arbitrary request headers.
- Response `ETag`, `Cache-Control` and `Last-Modified` are forwarded for
  `/api/intelligence/brand-assets/…` only. No other backend response can become browser-cacheable
  through the BFF.
- `/connect/enable-banking/callback`, `…/setup-callback` and `…/status-callback` are proxied by Web
  only when `!unifiedHost`. In unified mode `BankingApplication` maps them itself on the same
  Kestrel.

## Project layout

```text
src/FullWorth.Web            auth, sessions, BFF, static frontend (wwwroot), AuthDbContext
src/FullWorth.Backend        API + all finance domain modules (Modules/<Area>/…)
src/FullWorth.Banking        Enable Banking + FinTS connectivity, BankSyncWorker
src/FullWorth.FinTs          FinTS protocol library
src/FullWorth.Compensation.Core   the German tax/salary engine
src/FullWorth.Compensation.Wasm   browser bundle of that engine for the landing page (not referenced by Web)
src/FullWorth.CodexBridge    Node sidecar (server.mjs), optional `codex` Compose profile
src/Shared/SecretBootstrap.cs     Docker-secret file loading, shared by Web and Backend as a linked Compile item
```

`FullWorth.slnx` lists five source projects and four test projects
(`Backend.Tests`, `Banking.Tests`, `FinTs.Tests`, `Web.Tests`). `Compensation.Wasm` and
`CodexBridge` are outside the solution.

Backend domain modules under `src/FullWorth.Backend/Modules`: Accounts, Analytics, Audit,
BankConnections, Bootstrap, Budgets, Categories, Coach, Compensation, Contracts, Export,
FullWorthSpaces, Fx, Import, Ingestion, Intelligence, Loans, Merchants, Parity, Portfolio,
Preferences, Purchases, Push, Tax, Transactions, Users. `BackendApplication.UseFullWorthBackend`
maps all of their endpoint groups in one list — that method is the index of the HTTP surface.

`Modules/Parity` is the compatibility layer for imported Finanzguru/Finanzfluss-shaped features. Its
endpoints are facades over the canonical stack, not parallel storage, and they are the heaviest
users of raw SQL (`Modules/Parity/ParitySql.cs`).

Web modules under `src/FullWorth.Web/Modules`: Admin, Auth, Bootstrap, Import, Passkeys, Pin,
Purchases, Recovery, Sessions.

## Frontend

Vanilla ES modules in `src/FullWorth.Web/wwwroot`, no build step, no bundler, no linter or
formatter. Composition is `app.js` → `core/*` → `ui/*` → `features/*`; the rules live in
[FRONTEND_ARCHITECTURE.md](FRONTEND_ARCHITECTURE.md) and are enforced by
`tests/FullWorth.Web.Tests/FrontendArchitectureGuardTests.cs`.

The one exception to "HTML lives in wwwroot" is the import area:
`src/FullWorth.Web/Modules/Import/{ImportCenterPage,FinanzguruImportPage,BrokerPdfImportPage}.cs`
each hold a full HTML document in a C# raw string literal and serve it via `Results.Content`.
`ImportCenterPage` serves both `/settings/import` and `/settings/import/finanzguru`;
`FinanzguruImportPage` serves `/settings/import/finanzguru/xlsx`; `BrokerPdfImportPage` serves
`/settings/import/broker-pdf`. Editing those pages means editing C#, and each literal has to repeat
the whole stylesheet chain from `index.html` by hand — loading only `app.css` there left every
design token undefined.

Other shell pages (`/settings/security/passkeys`, account deletion) are real files served with
`SendFileAsync`; everything else falls through `MapFallbackToFile("index.html")` behind
`RequireAuthorization`.

## One database, three EF models

Compose points `ConnectionStrings__FullWorth` and `ConnectionStrings__AuthDatabase` at the same
PostgreSQL database. Three `DbContext`s share it, each with its own migration history:

| Context | Project | Tables | History table |
| --- | --- | --- | --- |
| `FullWorthDbContext` | Backend/Data | financial source of truth, 50 `DbSet`s | `public.__EFMigrationsHistory` |
| `IntelligenceDbContext` | Backend/Modules/Intelligence | AI, cloud, knowledge-pack, brand and signal metadata | `public.__EFMigrationsHistory_Intelligence` |
| `AuthDbContext` | Web/Data | ASP.NET Identity, sessions, passkeys, recovery | `auth.__EFMigrationsHistory` (schema `auth`) |

The split is deliberate: `IntelligenceDbContext` documents itself as holding
authorization-scoped *references* to users and spaces, and it never owns or cascade-deletes a
finance row. `InitializeFullWorthBackendAsync` migrates `FullWorthDbContext` (after
`PurchaseSchemaCompatibility.PrepareBeforeMigrationsAsync`), seeds defaults, then migrates
`IntelligenceDbContext`; the Web host migrates `AuthDbContext` separately before `app.Run()`.

`IntelligenceDbContext.OnModelCreating` remaps `DateTimeOffset` to a sortable binary converter when
the provider is SQLite, because the fast unit-style Intelligence tests run on in-memory SQLite,
which cannot order `DateTimeOffset` columns.

## Three data-access regimes in one schema

### 1. EF model with generated migrations

The default. Entities have `DbSet`s, migrations under `src/FullWorth.Backend/Migrations` use
`migrationBuilder.CreateTable(...)`, and `FullWorthDbContextModelSnapshot` tracks them.

### 2. Hand-written SQL inside EF migrations

Many later migrations are a single `migrationBuilder.Sql("""…""")` with idempotent
`CREATE TABLE IF NOT EXISTS` / `ALTER TABLE … ADD COLUMN IF NOT EXISTS`. They are versioned by the
migration chain, but the tables are not in the EF model at all — no `DbSet`, no snapshot entry — so
the only way to read or write them is raw SQL. `AssetValuations`, `ImportTransactionLinks` and the
whole real-estate, receipt-import and operational-registry surface work this way. 20 files under
`src/` use `FromSqlRaw`/`ExecuteSqlRaw`/`…Interpolated`, mostly `Modules/Parity` and
`Modules/Portfolio/Assets`.

Two follow-on rules exist because of this regime:

- **Frozen snapshot deltas.** Raw-SQL migrations do not update the model snapshot, so entities that
  *do* have CLR types (Coach, Tax Assistant, Enable Banking metadata, …) were missing from the
  baseline and tripped `PendingModelChangesWarning` at startup. `src/FullWorth.Backend/Migrations`
  therefore contains hand-written string-based snapshot deltas (`CoachSnapshot.cs`,
  `TaxAssistantSnapshot*.cs`, `FullWorthDbContextSnapshotV*.cs`, …). They stay string-based on
  purpose so later CLR model changes remain visible as pending changes.
- **`IModelCustomizer` instead of `DbSet`.** `FullWorthDbContext` is registered with
  `.ReplaceService<IModelCustomizer, CoachModelCustomizer>()`, which configures
  `SpendingReview`, `CoachConversation` and `CoachMessage` onto raw-SQL-created tables without
  adding them to the context's `DbSet` list.

### 3. Runtime DDL with no migration at all

The four Compensation stores (`CompensationStore`, `CompensationHistoryStore`,
`CompensationOtherIncomeStore`, `PayslipStore`) open a raw `DbConnection` from the EF context and
call an `EnsureSchemaAsync` that issues `CREATE TABLE IF NOT EXISTS` on every request. Five
snake_case, `jsonb`-payload tables exist only this way and appear in no migration and no snapshot:
`compensation_profiles`, `compensation_scenarios`, `compensation_history`,
`compensation_other_income`, `compensation_payslips`. Space/user authorization is enforced in C#
(`IsMemberAsync`) since these tables carry no foreign keys.

### Logic in the database

The hand-written migrations also install 28 `fullworth_*` PL/pgSQL functions and 23 `TR_*` triggers.
They are not decoration — they enforce real invariants: `TR_Assets_MirrorValuation` mirrors every
`Assets` value change into `AssetValuations` history, `TR_InvestmentTrades_PreventOversell` blocks
overselling a position, and the `TR_*_Kind` / `TR_*_Validate` triggers keep specialized asset detail
rows attached to the right asset kind and space.

Code that must bypass a trigger does so with a session GUC, not by dropping it:

```csharp
await db.Database.ExecuteSqlRawAsync("SET LOCAL fullworth.asset_valuation_suppress = 'on';", ct);
```

`fullworth.asset_valuation_method` and `fullworth.asset_valuation_user_id` are read the same way, so
a raw-SQL write can label the history row it causes.

Most trigger check violations surface as a generic 500. One is translated:
`PurchasePaymentAllocationConflictExceptionHandler` matches a `PostgresException` with
`SqlState == CheckViolation` whose message starts with `"Purchase payment allocation"` or
`"Purchase payment link must stay"` and rewrites it as a `409` problem. Any other database-enforced
rule that needs a specific HTTP status needs the same treatment.

## Financial data consistency

Source data: transactions, balances, accounts, ownership, space membership, assets, liabilities,
loans. Derived data: `NetWorthSnapshots` (materialized) and `FinancialSignals` (materialized).
Analytics, budgets, category/merchant reports and forecasts query source rows directly and keep no
independent copy.

The refresh pipeline is `src/FullWorth.Backend/Data/FinancialDataConsistency.cs`, wired as two EF
interceptors on `FullWorthDbContext`:

- `FinancialDataSaveChangesInterceptor.SavingChanges` runs `FinancialDataChangeDetector.Detect` over
  the change tracker and accumulates a `FinancialDataChangeSet` per `DbContext` instance.
- Processing happens after `SavedChanges` **only when there is no ambient transaction**. With an
  explicit transaction — bank sync and historical import both use one across several `SaveChanges`
  calls — `FinancialDataTransactionInterceptor.TransactionCommitted` processes it instead.
- `SaveChangesFailed`, `TransactionRolledBack` and `TransactionFailed` all `Drop` the pending set. A
  rolled-back write never refreshes derived data.
- `FinancialDataConsistencyCoordinator` runs on a fresh scope behind a `SemaphoreSlim(1,1)`, resolves
  account ids to space ids, then rebuilds net worth and enqueues signal refreshes. It swallows every
  non-cancellation exception: source data has already committed, so a snapshot failure must not turn
  a successful import into an error response.

### What the detector actually watches

| Entity | Trigger | Effect |
| --- | --- | --- |
| `FinanceTransaction` | `AccountId`, `Status`, `BookingDate`, `ValueDate`, `Amount`, `Currency` | net worth + signals, from the earliest of the old and new date |
| `FinanceTransaction` | `CategoryId`, `Counterparty`, `NormalizedCounterparty`, `Description`, `MerchantCategoryCode`, `IsIgnored`, `IsTransfer`, `CategorizationSource` | signals only |
| `BalanceSnapshot` | any of `AccountId`, `Amount`, `Currency`, `BalanceType`, `ReferenceDate`, `CapturedAt` | from today only — a balance refresh moves the anchor, not historical cash flow |
| `FinanceAccount` | insert | from today |
| `FinanceAccount` | delete, or a change to `FullWorthSpaceId`, `Currency`, `IsActive`, `IncludeInNetWorth` | full history |
| `AccountOwner` | insert, delete, or a change to `AccountId`, `UserId`, `OwnershipType` | full history |
| `FullWorthSpaceMember` | any insert, update or delete | full history |
| `Asset`, `Liability` | value/currency/`IncludeInNetWorth`/space | from today |
| `Budget`, `RecurringContract` | see the field lists in the detector | signals only, never net worth |

Add/delete always counts as a change; modify only counts when one of the listed properties is
marked modified, so a cosmetic edit does not trigger a rebuild.

Signal refresh is queued, not synchronous: `FinancialSignalRefreshQueue` writes an
`IntelligenceJob` debounced into 5-minute buckets, and returns without queueing anything when the
`signals` Autopilot feature is `Off` (`AutopilotRolloutSettings`; the shipped default is `On`).

### Historical net worth

`NetWorthSnapshotService.RebuildHistoryForUserAsync` reconstructs history per space *and per member*
from the newest trusted balance backwards:

- The anchor is the latest `BalanceSnapshot` per account, ordered by `CapturedAt` then by a
  `BalanceRank` preference (`interimAvailable` → `closingAvailable` → `closingBooked` →
  `interimBooked` → `expected`). An account with no balance row is not back-cast at all.
- Only accounts that are active, `IncludeInNetWorth`, owned by that member via `AccountOwner`, and
  not a portfolio-linked account are included.
- Transactions are excluded when `Status == "PDNG"` or `UseForBalanceHistory == false`, and are only
  applied to an account whose native currency equals both the snapshot currency and the transaction
  currency. Foreign booking amounts are never mixed into a native balance.
- Asset and liability values are not invented for past dates. `BuildPortfolioComponents` carries the
  last recorded component forward across gaps and leaves dates before the first recorded portfolio
  snapshot at zero. Investments are valued for today only, and their linked bank accounts are
  excluded from the bank component to avoid double counting.
- Rows older than the earliest surviving trusted source are deleted rather than kept as a stale
  synthetic wealth point from an earlier reconstruction pass.
- `NetWorthSnapshotWorker` runs every 6 hours and does a full `RebuildAllHistoryAsync` on its first
  iteration, so an existing installation gets its history rebuilt after a deployment without
  re-importing anything.

Finanzguru import writes rows with `UseForBalanceHistory = false`;
`FinanzguruAccountReconciliationService` flips them to `true` once the archive is reconciled to a
live account with a trusted balance. Until then an absolute historical net worth cannot be inferred
from those transactions alone.

### Where the pipeline does not reach

The detector reads the EF change tracker, so the two other data-access regimes are invisible to it:

- A raw-SQL write to a financial source table does not invalidate anything.
  `AssetValuationModule.CreateForUserAsync` updates `Assets.CurrentValue` with
  `ExecuteSqlInterpolatedAsync` and then calls `SaveChangesAsync` for an audit row only — the
  tracker holds no `Asset`, so no rebuild is scheduled and the change surfaces in net-worth history
  when the 6-hour worker next runs.
- `UseForBalanceHistory` is in neither field list, which is why the two Finanzguru reconciliation
  endpoints (`/api/import/finanzguru/accounts/{id}/confirm-history` and `…/link`) call
  `RebuildHistoryForUserAsync` explicitly. Those two are the only deliberate endpoint-level
  refreshes.

### Rule for new features

A new persisted financial source entity, or a new materialized financial read model, is added to
`FinancialDataChangeDetector` / `FinancialDataConsistencyCoordinator` in the same change. If the new
write path is raw SQL, it does not get consistency for free — either route the mutation through the
EF context or refresh explicitly. Do not add a page-specific refresh call to work around a missing
detector rule.
