# Backend architecture target for the alpha.40 cutover

> Status: target architecture and implementation plan only. This document does not change runtime behaviour. The current architecture remains documented in `docs/ARCHITECTURE.md`; execution and completion status are tracked in issue #104.

## Purpose

The `v1.3.0-alpha.40` cutover is the point at which FullWorth should stop carrying historical backend structure and database compatibility forward indefinitely. The goal is not a rewrite and not a move to microservices. The goal is a simple, readable and efficient modular monolith with one canonical runtime path per feature, one supported host, explicit persistence boundaries, a clean database baseline, and no pre-cutover fallback machinery.

`v1.3.0-alpha.39` is the last pre-cutover release. The first post-cutover release starts a new supported database epoch. Pre-alpha.40 persisted state is intentionally unsupported for this cut because the active installation is being rebuilt fresh.

## Design goals

The backend after the cutover must follow these rules:

- Keep FullWorth as one deployable application and one normal process. Do not split features into microservices.
- `FullWorth.Web` is the only supported executable host. Backend and Banking become in-process modules/libraries unless an actually supported external deployment requires otherwise.
- Every business operation has one canonical path. Do not keep parallel stores, fallback services, shadow configuration, old endpoints or duplicate persistence models.
- Organize code primarily by feature/domain (`Accounts`, `Transactions`, `Purchases`, `Banking`, `Budgets`, ...), then use only the few technical subfolders that make a module easier to understand.
- Endpoints translate HTTP to a use case. Services coordinate business operations. Persistence code owns database access. Provider adapters translate external systems into canonical FullWorth models/commands.
- Prefer plain .NET, EF Core and ordinary services. Do not introduce MediatR/CQRS/event-bus/generic-repository/base-manager frameworks merely to add layers.
- Add an abstraction only when it represents a real boundary or there are genuinely multiple implementations.
- Reuse common components only for genuinely common behaviour. Do not copy the same rule into multiple modules, but also do not create a universal helper for unrelated operations.
- Validate untrusted input, authorization, external-provider data and meaningful domain invariants once at the correct boundary. Do not repeat the same defensive checks through endpoint, service and persistence layers.
- Keep methods and files sized by responsibility, not by an arbitrary line limit. Large mixed-responsibility files must be split; tiny one-purpose files are not required when a small flat module is clearer.
- Fix the canonical path when it is wrong. Do not add a workaround, secondary code path, environment rescue, silent repair or compatibility fallback.
- Optimize hot paths with measured/query-driven changes. Do not add speculative caches, indexes or complexity.

## Non-goals

This cutover is not intended to:

- redesign every feature from scratch;
- create microservices or independent deployment units for domain modules;
- introduce a deep `Application/Domain/Infrastructure/Commands/Queries/Handlers` hierarchy;
- force every module into the same directory template when the module is small;
- preserve runtime compatibility solely for databases or configuration contracts older than the declared alpha.40 baseline;
- keep split-host code simply because it once existed;
- replace clear EF Core code with a generic repository abstraction;
- maximize the number of classes, interfaces, tests or validation branches.

## Current structure and concrete problems

### 1. The deployed runtime is unified, but the source still models split hosts

The supported deployment already runs Web, Backend and Banking in one unified container. The source still contains standalone Web SDK executables for `FullWorth.Backend` and `FullWorth.Banking`, each with its own `Program.cs`, Dockerfile and application settings.

Affected areas:

- `src/FullWorth.Web/Program.cs`
- `src/FullWorth.Backend/Program.cs`
- `src/FullWorth.Backend/Dockerfile`
- `src/FullWorth.Backend/appsettings.json`
- `src/FullWorth.Banking/Program.cs`
- `src/FullWorth.Banking/Dockerfile`
- `src/FullWorth.Banking/appsettings.json`
- split-host configuration and documentation that only exist for the retired deployment model

Target:

- `FullWorth.Web` is the sole executable composition root.
- Backend and Banking expose module registration/endpoint/application APIs rather than their own supported host lifecycle.
- Remove standalone host artifacts after verifying no current supported workflow consumes them.
- Keep external process boundaries only where they are real boundaries (for example an external banking provider, FullWorth Cloud, Codex Bridge when separate, Paperless or a market-data service).

### 2. The unified process still talks to itself over HTTP

`FullWorth.Web` currently retains BFF/backend/banking reverse-proxy mechanics and unified-host loopback handling. `FullWorth.Banking` also has `Backend/FullWorthBackendClient.cs`, which models Backend as an HTTP service even though the supported runtime places the modules in the same process.

Affected areas:

- `src/FullWorth.Web/Program.cs`
- BFF reverse-proxy and unified-loopback helpers used only for same-process forwarding
- `src/FullWorth.Banking/Backend/FullWorthBackendClient.cs`
- `src/FullWorth.Banking/Hosting/BankingApplication.cs`
- Banking services that use the backend client for operations available in the same process

Target:

- Preserve public/BFF route semantics where they are part of the supported Web contract, but do not perform HTTP calls to the same process to execute them.
- Replace Banking-to-Backend loopback calls with small canonical application interfaces/services owned by the relevant backend domain.
- Do not duplicate business logic inside Banking when removing HTTP. Banking should call the existing canonical operation directly.
- Delete loopback-only handlers/configuration once there is no in-process caller requiring them.

### 3. Composition roots know too much

`src/FullWorth.Backend/Hosting/BackendApplication.cs` registers a very large portion of the system in one central file: DbContexts, stores, services, providers, workers, HTTP clients and endpoints. `src/FullWorth.Banking/Hosting/BankingApplication.cs` has the same tendency for Banking. `src/FullWorth.Web/Program.cs` also carries a large amount of composition logic.

Target:

Each meaningful feature owns its registrations and route mapping, for example:

```text
Modules/Accounts/AccountsModule.cs
  AddAccountsModule(IServiceCollection, IConfiguration)
  MapAccountsModule(IEndpointRouteBuilder)

Modules/Purchases/PurchasesModule.cs
  AddPurchasesModule(...)
  MapPurchasesModule(...)
```

The top-level composition root should read like a list of application capabilities rather than a list of every concrete class in the repository. Module registration must stay explicit and easy to debug; do not add reflection-heavy automatic DI discovery simply to shorten code.

### 4. Several feature files have accumulated unrelated responsibilities

Examples from the current inventory include:

- `Modules/Accounts/AccountsModule.cs` contains domain entities/contracts, database access, balance/duplicate-account behaviour and endpoint concerns in one large file.
- `FullWorth.Banking/Services/BankSyncService.cs` has grown into a large orchestration/service implementation with too many banking-sync responsibilities.
- Large modules also exist around Analytics, BankConnections, Budgets, Categories, Purchases, Compensation and Intelligence.

This is not a request to split every file by line count. It is a request to separate responsibilities where a developer currently has to understand several unrelated concerns to change one behaviour.

Recommended structure for a larger module:

```text
Modules/Accounts/
  Models/
  Services/
  Persistence/
  Endpoints/
  AccountsModule.cs
```

Recommended structure for a small module:

```text
Modules/Tax/
  TaxModels.cs
  TaxService.cs
  TaxStore.cs
  TaxEndpoints.cs
  TaxModule.cs
```

Only create a subfolder when it groups multiple related files or makes navigation clearer. Do not create empty architectural layers.

### 5. Banking sync needs a narrow orchestrator and explicit stages

`BankSyncService` should not own every detail of provider retrieval, provider-to-domain mapping, account reconciliation, transaction/balance ingestion and persistence coordination.

Target flow:

```text
provider API / FinTS
        |
        v
provider adapter
(fetch + provider-specific validation)
        |
        v
canonical normalized banking result
        |
        v
BankSync orchestrator
        |
        +--> account reconciliation
        +--> balance ingestion
        +--> transaction ingestion/deduplication
        +--> sync-state/result update
```

Rules:

- Provider-specific field quirks stay in the provider adapter.
- FullWorth-wide account/transaction rules stay in their canonical backend services.
- The orchestrator controls order and the business unit of work; it should not become a second implementation of Accounts or Transactions.
- FinTS protocol details remain in `FullWorth.FinTs`; Banking adapts those protocol results to FullWorth's canonical banking model.
- No provider-specific fallback should silently invent data when the provider did not return it. Optional provider data is represented explicitly.

## Target module responsibilities

For substantial modules the preferred responsibility split is:

### Models / contracts

Contains domain records/entities/value types and API request/response contracts that are genuinely owned by that module. Do not create duplicate DTO and domain types when both carry exactly the same semantics without a boundary reason.

### Services

Contains use-case/business coordination. A service method should describe a recognizable user/system operation such as linking accounts, applying a manual balance, ingesting a bank batch or reviewing a purchase.

A service should not:

- expose EF implementation details to callers;
- contain provider-specific HTTP protocol parsing unless that is the service's actual boundary;
- reimplement authorization or validation that is already canonically enforced elsewhere;
- open a long database transaction around external HTTP calls.

### Persistence

Contains queries/stores and module-local EF configuration. Persistence code should make query shape and transaction intent understandable. It should not contain HTTP response construction or unrelated business workflows.

### Endpoints

Endpoints perform HTTP-specific work only: bind request, obtain caller context, call the canonical operation, translate the result into the HTTP contract. Complex LINQ/database mutation and business workflows do not belong in endpoint delegates.

### Module composition

`*Module.cs` registers the feature and maps its endpoints. It is the entry point for the feature, not the feature implementation itself.

## Dependency rules

The intended dependency direction is:

```text
FullWorth.Web                         only executable host
  |
  +-- Web authentication/admin/UI/BFF route adapters
  |
  +-- FullWorth.Backend
  |     |
  |     +-- Accounts
  |     +-- Transactions
  |     +-- Budgets
  |     +-- Purchases
  |     +-- Portfolio
  |     +-- Intelligence
  |     +-- other domain modules
  |
  +-- FullWorth.Banking
        |
        +-- Enable Banking adapter
        +-- FinTS adapter --> FullWorth.FinTs protocol library
```

Rules:

- Backend domain modules must not depend on Web.
- Banking may depend on narrowly exposed backend application contracts needed to ingest/update canonical financial data; it must not call Web or loop back over HTTP.
- Web owns authentication/session/UI concerns, not financial business logic.
- Shared code is for true cross-project primitives/contracts only. `Shared` is not a dumping ground for helpers that happen to be used twice.
- A feature should not reach directly into another feature's tables when a canonical operation is required to preserve that feature's invariants.
- Read-only cross-feature projections may query shared/core persistence where doing so is simpler and does not duplicate business rules.

## Database and persistence target

### Current problem: multiple schema evolution mechanisms

Backend startup currently has several ways to change schema:

1. regular EF Core migrations for the core model;
2. EF migrations for the separate Intelligence context;
3. `ParitySql.InitializeAsync` and its SQL-heavy parallel schema;
4. Compensation/benchmark database bootstrappers that perform runtime DDL;
5. pre-migration compatibility mutation such as `PurchaseSchemaCompatibility.PrepareBeforeMigrationsAsync`.

This makes the real source of schema truth difficult to identify and lets startup mutate old schemas through hidden compatibility code.

### Target: one explicit schema history per real DbContext

Known persisted EF contexts that must be included in the final inventory are:

- `FullWorthDbContext` for core financial/domain state;
- `IntelligenceDbContext` for intelligence state with its separate migration history;
- `AuthDbContext` for Web authentication/identity/admin configuration.

Before the alpha.40 baseline is generated, confirm that this is the complete context list.

Rules:

- EF migrations are the canonical schema evolution mechanism for these contexts.
- Database functions, triggers or specialized SQL are allowed only when they are the best canonical implementation of a real database invariant/performance requirement. They must then be versioned explicitly in migrations, not opportunistically created on each startup.
- Runtime DDL bootstrappers are removed.
- Pre-alpha.40 compatibility schema mutation is removed.
- Seeding/reference-data loading remains separate from schema creation.
- Do not create a DbContext per module. The existing context boundaries should remain unless the inventory finds a real ownership/lifecycle reason to change them.

### `FullWorthDbContext` cleanup

`FullWorthDbContext` currently contains a large list of DbSets and a large central `OnModelCreating`.

Target:

- Keep the core context.
- Move substantial per-entity configuration into module-local `IEntityTypeConfiguration<T>` classes where that improves readability.
- Apply those configurations explicitly/consistently from the context.
- Keep global conventions and truly cross-module relationships visible at the context level.
- Remove entities that exist only for unsupported pre-cutoff compatibility.
- Do not hide important domain relationships behind dynamic/reflection magic.

## Transactions and batching

The unit of a database transaction must match a durable business operation.

Rules:

- Never keep a database transaction open while waiting on an external HTTP/provider call.
- Do not `SaveChanges` for every row in an import or bank sync loop.
- Fetch external/provider data first, normalize and validate it, then write bounded batches.
- Use one transaction for rows that must be atomically consistent together, for example a purchase and its dependent lines or one bounded ingestion batch.
- Do not wrap an entire long-running multi-provider sync in one transaction solely for convenience.
- Queue/lease workers should claim and complete work with short transactions.
- Prefer database unique constraints/idempotency keys for race-safe deduplication rather than "check then insert" code duplicated in several callers.
- When an operation becomes too large for a safe transaction, split it at an explicit idempotent checkpoint rather than silently committing partial arbitrary rows.

## Index and query performance plan

Do not add indexes by intuition alone. The cleanup must review indexes against actual query patterns.

For every hot query family:

1. identify `WHERE`, `JOIN`, uniqueness and relevant `ORDER BY` columns;
2. inspect the existing index/constraint coverage;
3. remove only indexes proven redundant;
4. add composites only when their leading-column order matches the real query;
5. retain uniqueness as database constraints instead of application-only validation;
6. test representative hot queries with realistic row counts and PostgreSQL query plans when the implementation phase is run.

High-priority query families:

- account list/current balances and ownership/membership checks;
- transaction history, provider/import deduplication and transfer matching;
- bank connection/sync lookups;
- import/reconciliation matching;
- purchases, receipt imports and purchase-document matching;
- worker queues, leases and scheduled jobs;
- frequently refreshed analytics/net-worth projections.

Naming:

- Prefer predictable EF/PostgreSQL names such as `IX_<Table>_<Columns>`.
- Intentional unique constraints/indexes should have one consistent repository convention (for example `UX_<Table>_<Columns>` if an explicit name improves diagnostics).
- Do not create several differently named indexes over the same effective key.

Performance is not only indexes. During implementation also inspect:

- accidental N+1 query loops;
- loading full entities when a projection is enough;
- repeated identical queries in one request;
- `SaveChanges` inside item loops;
- unbounded history queries;
- unnecessary same-process serialization/HTTP;
- long lock/transaction scopes;
- background workers that poll or rescan more data than necessary.

## Legacy and compatibility removal inventory

### Banking instance settings

The FinTS runtime fallback was already removed. Remaining pre-cutover schema/code remnants must be removed with the clean baseline:

- `BankingInstanceSettings` entity/model/table;
- `FullWorthDbContext` mapping/DbSet;
- inert store/endpoint remnants;
- remaining DI/endpoint registration.

`FinTs:ProductId` in canonical instance configuration remains the only runtime source.

### Purchase schema compatibility

`src/FullWorth.Backend/Data/PurchaseSchemaCompatibility.cs` exists to recognize and rename an older purchase schema before normal migrations.

For the hard cutoff this must be deleted rather than expanded. An unsupported pre-alpha.40 database is rejected by the upgrade/schema epoch check; startup must not guess and mutate it into shape.

### Parity schema/module

`Modules/Parity` and `ParitySql` represent a parallel/compatibility-era schema and route layer. Do not blindly delete active functionality just because its filename contains `Parity`.

For every Parity table/route/model:

- if the behaviour is still an active FullWorth feature, move it to the canonical owning domain module and EF model;
- if it duplicates a canonical feature, merge callers into the canonical path and remove the duplicate;
- if it only supports retired compatibility, delete it.

Completion target: there is no permanent Parity schema subsystem and no `ParitySql.InitializeAsync` startup schema path.

### Compensation runtime DDL

Compensation and benchmark bootstrappers currently create/alter schema at runtime.

Target:

- express current required schema in the canonical EF model/migrations;
- move reference/benchmark data population to explicit seeding/import logic if needed;
- delete runtime DDL bootstrappers once the baseline owns that schema.

### Codex configuration aliases

`Modules/Intelligence/CodexBridgeConfiguration.cs` currently accepts historical/current configuration keys. During the cutoff, select the supported canonical configuration contract, update repository documentation/sample configuration, and remove aliases that exist solely for the old contract.

Do not remove intentional current provider failover behaviour merely because it is called a fallback; the rule is to remove compatibility/workaround paths, not deliberate product behaviour.

### Legacy deployment artifacts

Audit and remove artifacts that exist only for the retired split deployment, including `docker-compose.enable-banking-legacy.yml` and standalone Backend/Banking host files, if the final usage check confirms that no supported current workflow depends on them.

No replacement compatibility shim should be added for a deployment mode that the cutoff intentionally stops supporting.

## Detailed affected-area plan

| Area | Problem today | Target change |
| --- | --- | --- |
| `src/FullWorth.Web/Program.cs` | Large composition root; unified mode still composes split-service concepts and loopback/BFF forwarding | Keep Web as sole host; extract clear Web/auth/admin composition; map Backend/Banking modules directly; remove same-process loopback mechanics |
| Web BFF / unified loopback helpers | Same process is treated as a network service | Preserve supported HTTP contract while dispatching in process; delete loopback-only transport code |
| `src/FullWorth.Backend/Program.cs` | Standalone host remains after unified deployment became canonical | Remove after final usage check; Backend becomes a module/library |
| Backend Dockerfile/appsettings | Split deployment artifacts | Remove split-only artifacts/config after usage check; one canonical deployment contract |
| `src/FullWorth.Banking/Program.cs` | Standalone Banking host remains | Remove after usage check; Banking becomes an in-process module/library |
| Banking Dockerfile/appsettings | Split deployment artifacts | Remove split-only artifacts/config after usage check |
| `Hosting/BackendApplication.cs` | Central registration knows nearly every backend concrete service | Delegate registration/mapping to domain modules; keep only backend-wide infrastructure/composition |
| `Hosting/BankingApplication.cs` | Central Banking wiring plus Backend HTTP dependency | Register provider adapters/orchestrator cleanly; use direct canonical application contracts |
| `Banking/Backend/FullWorthBackendClient.cs` | Same-process Backend calls modeled as HTTP | Replace with narrow direct application interfaces/services; remove loopback client when unused |
| `Banking/Services/BankSyncService.cs` | Very large mixed-responsibility sync service | Split provider normalization, account reconciliation, balance ingestion, transaction ingestion and sync-state duties; retain a small orchestrator |
| `Banking/Services/IngFinTsService.cs` | Provider/use-case concerns need clear boundary after recent config fix | Keep FinTS-specific orchestration/provider adaptation only; canonical config via reloadable instance configuration; no legacy ProductId source |
| `Modules/Accounts/AccountsModule.cs` | Entities/contracts/persistence/business rules/endpoints accumulated together | Split by real responsibility; keep one Accounts module entry point; reuse one canonical balance/duplicate-account implementation |
| Other oversized modules (Analytics, BankConnections, Budgets, Categories, Purchases, Compensation, Intelligence) | Large files mix unrelated concerns | Review individually and split only along meaningful responsibility/use-case boundaries |
| `Data/FullWorthDbContext.cs` | Very large DbSet/model-configuration hub and still contains retired schema types | Keep core context; move readable module-local entity configuration; remove obsolete entities; keep cross-cutting conventions explicit |
| `Data/PurchaseSchemaCompatibility.cs` | Runtime pre-migration repair for old schema | Delete at hard cutoff; schema epoch rejects unsupported old DB |
| `Modules/Parity/**`, `ParitySql` | Parallel schema/route compatibility layer | Classify active behaviour into owning modules, merge duplicates, delete obsolete parts; remove runtime Parity schema initializer |
| Compensation DB bootstrappers | Runtime DDL creates/alters active schema | Move schema to EF baseline/migrations; keep data seeding separate; remove runtime DDL |
| `BankingInstanceSettings` remnants | Obsolete legacy table/model after canonical FinTS configuration fix | Remove entity/table/store/no-op endpoint/DI/model mapping in final schema |
| Intelligence/Codex legacy config aliases | Multiple historical config keys remain accepted | Choose/document one canonical contract and remove pre-cutoff aliases |
| Core/Intelligence/Auth migration histories | Long pre-cutoff migration history and multiple schema regimes | After all cleanup, generate real clean alpha.40 baselines for every confirmed persisted context |
| Worker/import/sync persistence | Potentially expensive row-by-row or broad transaction patterns must be verified | Audit `SaveChanges`, transaction scopes, batch size, idempotency and query shape in hot paths; simplify based on evidence |
| Existing DB indexes | Index set has grown feature-by-feature | Map indexes to real hot queries/constraints, remove only proven duplicates, add only justified composites and validate plans |
| Tests | Tests are feature-oriented but architecture/cutover invariants are not all enforced | Add focused architecture/cutover tests and fresh-database integration coverage; do not add low-value tests for trivial getters |
| `docs/MIGRATIONS.md` | Describes current normal migration workflow, not the future cutover epoch | Update during implementation after the new baseline/epoch mechanism actually exists; do not make documentation claim a not-yet-existing baseline |

## Implementation sequence

The order matters. In particular, the migration squash must happen after the target model is clean.

### Phase A - inventory and protect current intended behaviour

1. Inventory every executable host, module, DbContext, migration assembly, schema bootstrapper, runtime DDL path, compatibility helper, loopback HTTP path and provider adapter.
2. Classify legacy/compatibility items as `remove`, `move active behaviour`, or `keep current product behaviour`.
3. Identify the public/API behaviours that must remain after internal cleanup.
4. Add/strengthen targeted regression tests only where they are needed to protect meaningful current behaviour during moves.

Exit: there is no unknown schema/runtime path that could be accidentally dropped later.

### Phase B - establish the single runtime host

1. Make `FullWorth.Web` the explicit sole supported host.
2. Remove standalone Backend/Banking startup and deployment artifacts after confirming no supported use.
3. Remove same-process HTTP/loopback dependency between Web, Backend and Banking.
4. Preserve external API/BFF compatibility at the Web boundary without duplicating the business implementation.

Exit: one process, one composition root, no HTTP-to-self.

### Phase C - make module composition local and readable

1. Introduce/normalize `Add<Feature>Module` and `Map<Feature>Module` entry points where useful.
2. Move registrations from the giant backend/banking composition files to the owning feature.
3. Keep backend-wide infrastructure in the backend composition root and Web-wide authentication/session infrastructure in Web.
4. Avoid reflection/magic registration.

Exit: top-level startup reads as a short list of capabilities.

### Phase D - split mixed-responsibility code without changing behaviour

Prioritize the largest/highest-risk hotspots:

1. Banking sync.
2. Accounts.
3. Purchases/receipts.
4. BankConnections.
5. Compensation.
6. Intelligence/cloud jobs.
7. Analytics/Categories/Budgets and other modules where inventory shows mixed concerns.

Split by responsibility and canonical use case, not arbitrary line counts. Remove duplicated helpers while moving code.

Exit: common maintenance changes no longer require editing giant multi-purpose files.

### Phase E - canonicalize persistence

1. Move feature entity configuration out of the huge core model builder where helpful.
2. Move active Parity-owned behaviour/schema to canonical modules.
3. Move Compensation schema from runtime DDL into canonical EF modelling.
4. Confirm final DbContext/migration-history boundaries.
5. Separate schema evolution from data seeding/reference data.

Exit: every active table/column/index/function/trigger has one clear schema owner.

### Phase F - remove pre-cutoff legacy and compatibility

Remove, once their canonical replacement is proven:

- `PurchaseSchemaCompatibility`;
- remaining `BankingInstanceSettings` code/model/table;
- obsolete Parity compatibility paths;
- Compensation runtime DDL;
- legacy Codex/config aliases selected for cutoff;
- retired split-host deployment/config paths;
- other inventory items used only for pre-alpha.40 state.

Do not replace them with a new fallback.

Exit: startup does not repair, guess or shadow unsupported old state.

### Phase G - database/query/transaction performance pass

1. Audit hot EF queries and raw SQL.
2. Inspect N+1/repeated query patterns and projections.
3. Audit `SaveChanges` placement and batch writes.
4. Audit transaction duration and external I/O boundaries.
5. Match indexes to real predicates/order/uniqueness.
6. Validate important query plans with realistic data.
7. Simplify code where performance machinery is not justified.

Exit: performance choices have an observable reason and do not introduce parallel paths/caches without need.

### Phase H - perform #104 hard baseline cutover

Only after the code/schema cleanup is complete:

1. Remove superseded pre-cutoff EF migration chains for every confirmed context.
2. Generate real EF alpha.40 baseline migrations from the final model. Do not hand-author a fake baseline.
3. Add the machine-readable `.agent/upgrade-policy.yaml` with epoch 1 / alpha.40 support floor.
4. Persist/validate the schema/upgrade epoch as designed.
5. Make startup fail clearly for unsupported pre-alpha.40 persisted state instead of attempting repair.
6. Update `docs/MIGRATIONS.md` and operator documentation to state the breaking reset point.

Exit: a fresh database is created entirely from the new baseline and unsupported old state is rejected clearly.

### Phase I - final validation

Before merging the implementation/cutover:

1. Create a fresh PostgreSQL database.
2. Apply/start from the alpha.40 baseline and verify the complete schema.
3. Verify startup and bootstrap/admin creation.
4. Run representative flows for accounts, manual balances, import/reconciliation, banking/FinTS, transactions, purchases/receipts, net worth/portfolio and scheduled/background work relevant to changed areas.
5. Run targeted test projects for changed modules.
6. Run the required repository Release build:

```bash
dotnet build FullWorth.slnx -c Release --nologo
```

7. Inspect startup logs for hidden schema mutation, fallback warnings, repeated loopback calls or failed workers.

Exit: alpha.40 is a clean new supported base rather than an old system hidden behind new migration metadata.

## Completion criteria

The backend cleanup and #104 are complete only when all of the following are true:

- `FullWorth.Web` is the single supported executable host.
- Backend and Banking do not use HTTP to call the same process.
- There is one canonical implementation path for each supported business operation.
- Central composition files no longer manually know every feature service where module-local registration is clearer.
- Major mixed-responsibility hotspots have been split along real responsibilities, without replacing them with dozens of meaningless tiny layers.
- No unsupported pre-alpha.40 runtime compatibility mutator/fallback remains.
- `BankingInstanceSettings` is physically removed from the active schema/model.
- `PurchaseSchemaCompatibility` is gone.
- The permanent Parity schema subsystem is gone; still-active behaviour has a canonical owner.
- Compensation and other active schema are not created/altered through runtime DDL bootstrappers.
- Every active persisted schema has one canonical migration owner.
- Core, Intelligence, Auth and any other confirmed context have real post-cleanup alpha.40 baseline migrations.
- `.agent/upgrade-policy.yaml` truthfully describes the implemented epoch/support floor.
- Unsupported old persisted state fails with a clear operator-facing error rather than being silently repaired.
- Batch/import/sync hot paths do not save each item individually without a demonstrated reason.
- External network I/O is not performed while holding long DB transactions.
- Indexes correspond to real query/constraint needs; redundant/speculative indexes are not retained merely "just in case".
- Important hot queries avoid obvious N+1/unbounded/repeated-query patterns.
- A completely empty PostgreSQL installation can reach a working current FullWorth system using only the new baseline and normal bootstrap/seeding.
- Relevant targeted tests and the required Release build pass.

## Rule for future backend work

Once alpha.40 establishes this base, new backend work should extend the owning domain module and its canonical path. If a change appears to require a second store, second config source, compatibility endpoint, duplicate model, fallback table or loopback service, first determine why the canonical path cannot support the requirement and fix that path instead.

Future migration cutoffs may deliberately advance the supported schema epoch and squash history again. Those cutoffs must update the support declaration and baseline together; the repository should never claim compatibility that its code/schema does not actually support.
