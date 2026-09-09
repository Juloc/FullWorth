# Testing

## What exists

Four xUnit projects, all in `FullWorth.slnx`:

| Project | Tests | Needs Postgres |
| --- | --- | --- |
| `tests/FullWorth.Backend.Tests` | 1147 | yes |
| `tests/FullWorth.Web.Tests` | 548 | yes |
| `tests/FullWorth.Banking.Tests` | 140 | no |
| `tests/FullWorth.FinTs.Tests` | 5 | no |

## What does not exist

- **No linter and no formatter.** There is no `.editorconfig`, no ESLint/Prettier config and no
  `package.json` anywhere in the repo (the CodexBridge sidecar is dependency-free `.mjs`), and no
  workflow runs `dotnet format`. The closest substitutes are C# guard tests that grep the shipped
  frontend:
  `tests/FullWorth.Web.Tests/FrontendArchitectureGuardTests.cs` (native dialogs, direct BFF calls,
  `fetch` monkey-patches, native `confirm`, DOM patch observers, patch-layer file names, CSS layer
  order, bootstrap ownership — each with an explicit allow-list of pre-existing offenders) and
  `Security/Headers/SecurityHeadersSourceAuditTests.cs` (inline script/style/handler, `javascript:`,
  `eval`, `new Function`).
- **No automated browser or end-to-end test.** Nothing drives a browser. The Playwright assemblies
  under `FullWorth.Backend.Tests/bin` are transitive from the Amazon connector's project reference,
  not a test dependency: no test type mentions `IPage`, `BrowserType` or `Playwright`, and no test
  project has a `Microsoft.Playwright` `PackageReference`.
  Web tests use `WebApplicationFactory` + `TestServer` with the backend and banking HTTP
  clients replaced by stub handlers, and assert on served bytes (HTML/JS/CSS/JSON) rather than on a
  rendered page. Everything visual is verified by a human — see
  [UI verification without credentials](#ui-verification-without-credentials).
- **No automatic CI.** `ci.yml` is `workflow_dispatch` only, by explicit policy
  ([CONTRIBUTING.md](../CONTRIBUTING.md)), and it is **not** a tag gate — nothing blocks a red release.

## Running the tests

The two integration suites need `FULLWORTH_TEST_POSTGRES` pointing at an isolated PostgreSQL 18.
Without it they throw `FULLWORTH_TEST_POSTGRES must point to the isolated PostgreSQL test server` —
that is environmental, not a regression. Pure unit tests (calculators, parsers, option validators)
run without it.

```bash
docker run -d --name fullworth-ci-pg \
  -e POSTGRES_USER=fullworth_test -e POSTGRES_PASSWORD=fullworth_test_password \
  -e POSTGRES_DB=fullworth_test -p 5432:5432 --shm-size=1g \
  postgres:18 -c max_connections=500 -c fsync=off -c synchronous_commit=off \
              -c full_page_writes=off -c shared_buffers=256MB

export FULLWORTH_TEST_POSTGRES="Host=localhost;Port=5432;Username=fullworth_test;Password=fullworth_test_password;Command Timeout=120;Timeout=30"
```

```bash
dotnet build FullWorth.slnx --configuration Release

dotnet test tests/FullWorth.Backend.Tests/FullWorth.Backend.Tests.csproj -c Release
dotnet test tests/FullWorth.Web.Tests/FullWorth.Web.Tests.csproj      -c Release
dotnet test tests/FullWorth.Banking.Tests/FullWorth.Banking.Tests.csproj -c Release
dotnet test tests/FullWorth.FinTs.Tests/FullWorth.FinTs.Tests.csproj  -c Release

# normal working loop: always filter
dotnet test tests/FullWorth.Backend.Tests/FullWorth.Backend.Tests.csproj --nologo --filter "FullyQualifiedName~Contracts."
```

Prefer a filter locally. Every Backend test class starts its own `WebApplicationFactory` host, and a
local Docker Desktop struggles under the whole suite — run the full sweep in CI.

### How the test databases work

`BackendWebApplicationFactory` applies all migrations **once** into a template database
(`fullworth_test_template_backend`, dropped and rebuilt each run) and then clones it per test class
with `CREATE DATABASE … TEMPLATE`, a file-level copy. The app's start-up `MigrateAsync` then finds
everything applied and no-ops. `FULLWORTH_TEST_NO_TEMPLATE=1` restores the old
migrate-from-scratch-per-class behaviour for A/B comparison.

`FullWorthWebFactory` gives each Web test host its own `fullworth_web_<guid>` auth database, migrated
by the app on start-up.

Both cap the connection pool at 10 with a 5-second idle lifetime: the full suite opens hundreds of
pools, and the defaults exhaust `max_connections` ("too many clients already").
`tests/FullWorth.Backend.Tests/xunit.runner.json` additionally caps `maxParallelThreads` at 2. The
default (core count) runs ~8 hosts at once on a dev box and the connection burst produces a rotating
cast of transient failures that all pass in isolation. 2 matches the CI runner, so this is CI-neutral.

## CI

`gh workflow run CI` (or the Actions tab). `.github/workflows/ci.yml` runs:

- **Build (all projects)** — `dotnet build FullWorth.slnx --configuration Release`, so a break in a
  project no test references (e.g. `FullWorth.CodexBridge`) still fails.
- **Backend · \<shard\>** — the Backend suite split across 7 parallel jobs by test namespace
  (`purchases`, `api`, `contracts`, `intelligence`, `portfolio`, `analytics`, `rest`), each with its
  own Postgres container, keeping wall-clock around 5 minutes. `rest` is the **complement** of the
  other six (a NOT-filter), so a new namespace can never silently fall out of CI.
- **Web + Banking + FinTs** — one job, sequential.

`Juloc/FullWorth` is public, so the parallel jobs cost no Actions minutes.

A release is requested by writing the tag into `ops/release-request.txt` and pushing to `main`;
`alpha-release-request.yml` validates it against `^v[0-9]+\.[0-9]+\.[0-9]+-alpha\.[0-9]+$`, creates the
tag, and `release.yml` publishes the images. CI does not run on that path — dispatch it yourself
first. See [Release](RELEASE.md).

## Frontend checks

The frontend is vanilla ES modules in `src/FullWorth.Web/wwwroot` with **no build step**, so nothing
compiles it and a syntax error only surfaces in the browser. Check a changed module explicitly:

```bash
cp src/FullWorth.Web/wwwroot/features/analytics.js /tmp/c.mjs && node --check /tmp/c.mjs
```

Three pages are the exception: `src/FullWorth.Web/Modules/Import/ImportCenterPage.cs`,
`FinanzguruImportPage.cs` and `BrokerPdfImportPage.cs` keep their HTML in a C# raw string literal and
map it onto `/settings/import*` routes. Editing those means rebuilding the host — and it is why the UI
harness parses the literal out of the source instead of using a copy.

After any frontend change, load the page and check the browser console for module errors. The
`fullworth-test` container on <http://localhost:8099> bind-mounts `wwwroot`, so edits are live without
a rebuild.

## UI verification without credentials

`ops/ui-harness/server.mjs` serves the **real** `wwwroot` against canned fixtures, with no login and
no database:

```bash
node ops/ui-harness/server.mjs        # http://127.0.0.1:8095
node ops/ui-harness/server.mjs 8096
```

It injects `fixtures.js` before `app.js` to stub `/bff/*` and `/api/*` (unknown endpoints return `[]`,
which every view tolerates), answers the antiforgery endpoint, and logs write bodies — so commit and
save paths can be walked end to end. `security/secure-fetch.js` captures `nativeFetch` at module load,
so a page-side `fetch` override never sees the writes and the **server log is the only place the real
payload appears**.

Three deliberate behaviours, each of them a lesson from a wrong conclusion:

- It **parses the C#-inlined import pages out of the source** (the raw string literal *and* the
  `MapGet` routes) at start-up, so an edited page is served edited and a new route appears by itself.
  A hand-copied HTML file once made a change look like it had no effect.
- It **announces its SPA fallback** with an `X-Harness-Fallback` response header. If that header is
  present, the harness had no page and you are looking at the shell — not at a broken product.
- Fixture data is deliberately awkward (a long counterparty name, a row that fails validation, a
  duplicate), because a layout only breaks on the awkward cases.

Two `server.mjs` details worth knowing before debugging it: fixture keys match as **substrings**, so a
key that is a substring of another path must come first (`rollback` before `import-jobs`), and the map
applies only to `/bff/` and `/api/` paths — without that guard it also answered
`/features/accounts.js` with JSON. And `/auth/admin/*` is answered by the server, not by the
browser-side stub, because the admin page talks to it directly rather than through the BFF.

Measure, do not eyeball. "Is this control 44 px on a phone" is a question for the harness plus devtools,
not for a screenshot.

## Accessibility

### Enforced by tests

`tests/FullWorth.Web.Tests/Accessibility/AccessibilityGuardTests.cs` locks the statically checkable
[UI/UX spec](UI_UX_SPEC.md) §25 invariants against `wwwroot`:

- `index.html` has `class="skip-link"` targeting `href="#main"` and an `id="main"` landmark;
- every stylesheet `index.html` actually links (discovered by regex, not a hand-picked list) together
  contains `:focus-visible` and `prefers-reduced-motion` rules;
- `app.js` sets `aria-current` at least twice — desktop sidebar and `#bottom-nav button[data-view]`;
- `core/i18n.js` updates `document.documentElement.lang`;
- `features/analytics.js`, `networth.js`, `loans.js` and `contracts.js` each emit `role="img"` plus an
  `aria-label` on their SVG charts, so chart meaning is never carried by colour alone.

`WealthUiBaselineTests.AccessibilityReleaseFixesAreLoadedLocalizedAndCached` locks
`ui/accessibility-release.js`: it is imported by `features/wealth-real-estate.js`, carries both the
German and English label sets ("Buchungen durchsuchen" / "Search transactions"), sets
`scope="col"` on transaction table headers, gives icon-only `×`/`✕`/`✖` close buttons a localized
`aria-label`, uses a `MutationObserver` to cover dialogs created after page load, and is listed in the
service-worker static shell. The same suite asserts `sw.js` still excludes `/api`, `/bff`, `/auth`,
`/share` and `/connect` from the offline cache.

`FinanceUxGapClosureBaselineTests` asserts a `min-height:44px` touch target; `ResponsiveLayoutTests`
asserts the tablet/mobile breakpoints, horizontal table scrolling, the fixed bottom nav with exactly
five primary destinations, and that every declared action button is wired in `app.js`.

Beyond that, the shipped app already provides semantic landmarks (`aside`, `nav`, `main`, `header`,
native tables), native `button`/`input`/`select`/`dialog` elements, `role="status"` toasts and
`aria-pressed` privacy controls.

### Not enforced — needs a human

None of the above is a WCAG claim. Before a release, on desktop **and** a narrow viewport:

1. keyboard-only pass through sidebar/bottom nav, Wealth overview, the asset-type chooser and every
   specialized asset dialog;
2. focus stays visible, dialog focus is contained, Escape closes and focus returns to a sensible
   trigger;
3. axe or Lighthouse on at least Dashboard, Buchungen, Vermögen, one real-estate detail, one
   specialized asset detail and one investment security detail;
4. light **and** dark contrast, especially muted text, semantic status text and disabled controls;
5. NVDA/VoiceOver smoke test: navigation, one transaction row, Wealth totals, one modal, one form
   validation message, one toast;
6. privacy mode masks sensitive wealth labels and identifiers in every specialized asset and property
   view.

A regression here blocks release even when the static guards pass.

## Performance

These checks run against a throwaway Postgres, never a live bank or provider.

### Enforced by tests

`tests/FullWorth.Backend.Tests/Performance/TransactionQueryPerformanceTests.cs` has two tests:

- `ScopedTransactionFilters_StayResponsive_OverModerateDataset` runs in the **normal suite**. It seeds
  4,000 transactions across two accounts and one account group, then asserts a scoped query combining
  category descendants, account group, merchant, amount range, status, direction and date range
  returns in under 2 s.
- `TransactionSearch_StaysResponsive_OverLargeDataset` is **opt-in** and returns immediately unless
  `FULLWORTH_PERF` is `1`/`true`. It seeds `FULLWORTH_PERF_TX` transactions (default 100,000) in
  5,000-row batches and asserts a filtered + sorted top-200 query returns in under 2 s.

Note that the opt-in test skips by plain `return`, so it reports as **passed**, not skipped — a green
run says nothing about the load path unless `FULLWORTH_PERF=1` was set.

```bash
export FULLWORTH_PERF=1
export FULLWORTH_PERF_TX=100000
dotnet test tests/FullWorth.Backend.Tests/FullWorth.Backend.Tests.csproj -c Release \
  --filter "FullyQualifiedName~Performance"
```

Keep every assertion a generous ceiling — a regression detector, not a micro-benchmark.

### Manual load validation

Representative dataset: a few users/spaces with shared accounts, dozens of accounts, 100k+
transactions with normalized counterparties, plus purchase items, category rules and net-worth
snapshots. Measure transaction search/filter/sort, dashboard, category and merchant analytics,
JSON/CSV export, and batch ingestion; record wall-clock p50/p95 for each.

Method: seed, `ANALYZE`, time cold then warm, and for anything over target capture the
`EXPLAIN (ANALYZE)` plan and name the missing index or the N+1 **with evidence** before changing
anything. Never weaken the per-space/owner authorization filters for speed.

The schema already carries the indexes these queries rely on — verify under load rather than adding
more speculatively (`FullWorthDbContext` declares 79 indexes):

- `Transactions`: `BookingDate`, `CategoryId`, `NormalizedCounterparty`, `TransferGroupId`,
  unique `(AccountId, ExternalKey)`;
- scoping: `Accounts.FullWorthSpaceId`, `AccountOwners` primary key `(AccountId, UserId)`,
  `FullWorthSpaceMembers` primary key `(FullWorthSpaceId, UserId)` plus the reversed
  `(UserId, FullWorthSpaceId)` index;
- feature tables: `Budgets (IsActive, CategoryId)` + `FullWorthSpaceId`,
  `NetWorthSnapshots (Date, Currency)` + `(FullWorthSpaceId, UserId)`,
  `PriceChangeSuggestions (ContractId)`, unique `PushDevices (FinanceUserId, Endpoint)`.

## Release smoke

There is no automated e2e, so the post-deploy pass is manual. Before merging a release candidate:
`dotnet build FullWorth.slnx --configuration Release`, the four suites above,
`docker compose --env-file .env.example config --quiet`, and `ops/restore-test/verify-restore.sh`.

`verify-restore.sh` is safe to run against a live installation: it restores the latest Postgres dump
into an **ephemeral** container and the latest purchases archive into a **throwaway** volume, then
checks migration history, core tables, one relationship and the file manifest. Exit 0 is a pass. It
never touches the live database or volume — that is `ops/backup/postgres/restore.sh`, which refuses to
overwrite the live database without `--force`.

After deploying:

- **Process** — all containers start, migrations complete, `GET /health` returns `status=ok`, no
  repeating migration or worker exception loop in the logs.
- **Core reads** — login, dashboard, accounts, transactions, contracts, budgets, analytics, net worth.
- **Mutations** — a normal transaction edit saves; categorization-rule preview works; contract
  detection and transfer detection open.
- **Coach / Intelligence** — Coach works with **no** AI provider configured (and with one, if the
  instance uses it); the Intelligence admin page opens for an admin; scheduled Intelligence workers do
  not fail because Autopilot is unconfigured.
- **Banking**, where configured — provider status loads, an account sync completes, imported
  transactions stay visible.
- **PWA** — the service worker installs, and no `/api` response lands in the cache.

Rollback is redeploying the previous image. Do not downgrade the database: the Autopilot tables
(`FinancialSignals`, `FinancialSignalStates`) are additive and hold no finance source of truth, and
merged-contract history (`MergedIntoContractId`) with unmerge support predates the merge-execution
feature.

### Autopilot rollout switches

The flags, their defaults and who reads them are in
[AI in FullWorth](AI_AUTOPILOT.md#rollout-flags). An invalid value throws instead of silently
enabling an unknown state, so a typo in `Autopilot__Features__*` surfaces immediately rather than in
behaviour.

Rollback levers, in increasing severity:

- `Autopilot__Features__contract-merge-execution=off` — hides the merge button and makes
  `POST /api/contracts/merge-execute` return 403, leaving insights and signals running.
- `Autopilot__Features__insights=off` — `/api/insights` returns 404 and the dashboard section
  disappears; deterministic signal generation continues.
- `Autopilot__Features__signals=off` — queued signal jobs become safe no-ops.

Properties worth re-checking after an Autopilot change, because they are what keeps the feature
harmless: no signal is generated merely by starting the application; signal jobs complete with no AI
credential or provider configured and create no `AiRun` row; the merge **preview** never writes
`MergedIntoContractId` and never calls `SaveChanges`; execution requires the exact preview token and
returns 409 when contract or payment state moved underneath it; a retried successful merge returns 200
with `alreadyApplied=true`; read-only members see the preview but cannot execute; mixed-currency
contracts cannot be merged; dismiss/snooze/read affect only the authenticated user's signal state and
never finance source data; and a non-member gets 404 for another space's insights.

## See also

- [Security architecture](SECURITY_ARCHITECTURE.md) — what the security tests are protecting
- [Architecture](ARCHITECTURE.md) — the module and BFF structure the integration tests exercise
- [Migrations](MIGRATIONS.md) — schema change rules
- [Operations](OPERATIONS.md) — deploy, backup, restore, secret rotation
- [Banking](BANKING.md) — banking/FinTS coverage gaps and the operator-run live bank validation
- [Frontend architecture](FRONTEND_ARCHITECTURE.md) — the structure the guard tests enforce
