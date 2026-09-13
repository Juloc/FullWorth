@AGENTS.md

# FullWorth

Self-hosted personal finance platform: .NET 10 backend + **vanilla-JS** frontend (no framework, no build step). Public repo `Juloc/FullWorth`. German UI.

Cross-agent workflow, coordination and backlog rules come from `AGENTS.md`. This file keeps FullWorth-specific durable operating context.
`docs/IMPROVEMENT_PLAN.md` is a transitional legacy backlog while issue #102 migrates remaining live items to GitHub Issues. Do not add new work there.

## Repo layout

- `src/FullWorth.Backend` — API + all domain modules (`Modules/<Area>/…`); owns `FullWorthDbContext` and `IntelligenceDbContext`
- `src/FullWorth.Web` — auth/shell host **and the BFF**; owns ASP.NET Identity (schema `auth`), passkeys, PIN, sessions. The frontend lives in `src/FullWorth.Web/wwwroot`
- `src/FullWorth.Banking`, `src/FullWorth.FinTs` — bank connectivity
- `src/FullWorth.Compensation.Core` — the German payroll engine, **deliberately dependency-free** so it also compiles to WASM
- `src/FullWorth.Compensation.Wasm` — browser bundle of that engine for the landing page; **not in `FullWorth.slnx` and not in any Dockerfile** (needs the `wasm-tools` workload)
- `src/Shared/SecretBootstrap.cs` — not a project; `<Compile Include>`-linked into Backend, Web and Banking. Docker-secret file config source + fail-closed `RequireSecret`
- `src/FullWorth.CodexBridge` — the Node Codex bridge (`server.mjs`) that runs `codex exec`. **Not a separate container since alpha.36:** `src/FullWorth.Web/Dockerfile` builds it into the app image, it runs as the `codex` user on `127.0.0.1:8099`, and `ops/docker/fullworth-codex-launcher` keeps it asleep until something arms it. Exposes `POST /execute` (`{systemInstruction, inputJson, jsonSchema, model}` → `{success, outputJson}`), `POST /scan`, `GET /status`, `POST /auth/start`, `GET /auth/{id}`, `POST /logout`, `GET /models`, `GET /logs/recent`. Auth: `X-FullWorth-Internal-Key` + `X-FullWorth-Codex-Scope` (64-hex sha256)
- `tests/FullWorth.{Backend,Web,Banking,FinTs}.Tests`
- `ops/release-request.txt` — release trigger (see below)
- `ops/ui-harness/` — serves the real `wwwroot` against fixtures, no login needed

## Build & test

```bash
dotnet build FullWorth.slnx -c Release --nologo
dotnet test tests/FullWorth.Backend.Tests/FullWorth.Backend.Tests.csproj --nologo --filter "FullyQualifiedName~SomeTests"
```

**Gotcha:** integration tests need `FULLWORTH_TEST_POSTGRES` pointing at an isolated Postgres 18. Without it they fail with `FULLWORTH_TEST_POSTGRES must point to the isolated PostgreSQL test server` — that is **environmental, not a regression**. Pure unit tests (calculators, parsers) run fine without it. A `fullworth-ci-pg` container usually listens on :5432:

```bash
export FULLWORTH_TEST_POSTGRES='Host=localhost;Port=5432;Username=fullworth_test;Password=fullworth_test_password'
```

Always filter to the relevant tests; the full suite is 1 840 tests and local Docker struggles under it. Counts: Backend 1147, Web 548, Banking 140, FinTs 5. Banking and FinTs need no database.

**That test server grows.** Each test class clones the migration template into its own database and a per-class DROP was measured as a real slowdown, so clones are left behind — 4 241 of them and 61 GB by 2026-09-11, which filled the disk and took the Docker engine down with it. `BackendWebApplicationFactory.PurgeAbandonedDatabases` now drops leftovers older than six hours once per test process. If the data directory is ever huge again, count `pg_database` before suspecting Docker.

There is **no linter, no formatter and no automated browser/e2e test** in this repo. `ci.yml` is `workflow_dispatch` only — it is **not** a tag gate and does not run on push, so `main` can be red unnoticed. Run it before requesting a release.

## Frontend rules

Six rules carry the structure. They are few on purpose, and each one holds something that
otherwise falls back quietly — not out of ill will, but because the shortcut is always cheaper in the
moment. `FrontendStructureGuardTests`, `MenuParityTests` and `LayoutStabilityTests` are the version
that argues back.

1. **No layout shift.** Space is reserved, never created afterwards: `visibility:hidden` plus a
   `min-height` rather than `hidden`, images with dimensions, nothing inserted into markup that is
   already drawn. Whatever has to be true before the first paint belongs in `app/boot.js` — a classic
   `<script>` in `<head>`, deliberately not a module.
2. **No loading afterwards.** No `<link>` from JavaScript, no `import()`. Everything is there at the
   first paint or it does not belong.
3. **One page is one folder** under `pages/`, holding `page.html`, `page.css` and `page.js`. The folder
   path is the address: `pages/settings/security/passkeys` answers `/settings/security/passkeys`.
   Anything two pages share goes to `components/` or `styles/`. This replaces the old rule against
   `features/<name>/` subfolders.
4. **One menu source**, `wwwroot/app/menu.js`. The sidebar, the four quick targets and the whole tree
   behind "Mehr" are three renderings of that one list, never three lists. Nothing is added to the
   menu at runtime.
5. **One document.** A new page is a page inside the shell, not a standalone HTML file.
   `ops/generate-shell.mjs` writes the menu and every page into `index.html`; run it after adding a
   page and `--check` keeps the file and the folder tree together.
6. **Nothing unnecessary.** No CSS property that changes nothing, no rule that only overrides another,
   no duplicated code, no check for cases that do not occur. The base inherits; a component only adds
   the difference. A page that has moved is *shorter* than it was — otherwise it was only relocated.

Beyond those:

- Vanilla ES modules in `wwwroot`, **no build step**. Syntax-check with:
  `cp file.js /tmp/c.mjs && node --check /tmp/c.mjs`
- The layers are `styles/` (`tokens` → `reset` → `shell` → `components` → `responsive`, then
  `styles/features/*`, then `styles/mobile-polish.css` last) plus each page's own `page.css`.
  **Tokens only** — no hardcoded colours, no frameworks, no DOM hacks. Four stylesheets still sit at
  the `wwwroot` root outside the scheme; the list in the structure guard may get shorter, never longer.
- `components/` knows neither a page nor the server. `features/` may. `core/` is the system layer and
  knows nothing visual.
- The CSP allows `style-src-attr 'unsafe-inline'`, so a style *attribute* works — but prefer tokens and classes anyway. A `<style>` block is blocked.
- **Buttons: use the shared module roles only** — `.btn` + `.btn-primary` / `.btn-secondary` / `.btn-danger` (`styles/components.css`). Do not hand-roll button styling. (`.primary-action` in `shell.css` is the older variant still used app-wide.)
- **Not all markup is in `wwwroot` yet:** `Modules/Import/{ImportCenter,FinanzguruImport,BrokerPdfImport}Page.cs` still keep their HTML in C# raw string literals, and `ops/ui-harness` parses those literals so an edited inlined page is served edited. Those three are the last standalone documents; they move to `pages/settings/import/` and then this note goes away.

### Live UI verification (use this!)
Two ways, neither needs credentials:

- `node ops/ui-harness/server.mjs` → <http://127.0.0.1:8095> serves the real `wwwroot` against fixtures. Use this to measure layout and open dialogs.
- A local dev stack sits next to this repo (`../local`) and serves <http://127.0.0.1:8100>; it **bind-mounts `src/FullWorth.Web/wwwroot`**, so frontend edits are live immediately — no rebuild, no redeploy.

Check the browser console for module errors after any frontend refactor. Measure, do not eyeball: `position:fixed` elements have `offsetParent === null`, so that is not a visibility test.

### Compensation ("Gehalt") feature
`features/compensation-shared.js` is the **single source of truth** for the profile↔form mapping (`readProfile` / `fillProfile`), car-factor derivation, benefit rows, formatters and api/notify. The calculator, history and extended views all import from it — **never re-implement these per view** (they used to be triplicated and silently diverged).

## Money rules that must not be broken

- **Parse imported numbers with `Modules/Parity/ImportNumber.cs`, never with a culture.** Every importer used to bring its own parser, and two of them read `"1234.56"` as `123456` because a German culture with `AllowThousands` accepted the dot as a group separator and .NET does not validate group sizes.
- **Never overwrite an original-currency amount with a base-currency conversion.** A conversion is a derived value.
- **An account's own balance must reach the user without a link to a separate asset entity.** Correct display may never depend on a manual post-import step.
- **A missing FX rate marks the result incomplete** — never 1:1, never 0.
- **A historical value is stored as of its date**; do not recompute the past with today's rate.

## Release

1. Bump `ops/release-request.txt` to `vX.Y.Z-alpha.N`, commit, push to `main`.
2. `Alpha Release Request` workflow creates the tag → `Release` workflow publishes `ghcr.io/juloc/fullworth`, amd64 + arm64 (~4–16 min). `fullworth-codex` is no longer built — the bridge is in the main image.
3. Verify: `gh run list`, then `docker manifest inspect ghcr.io/juloc/fullworth:1.3.0-alpha.N`.

**A failed release burns its tag** (alpha.11 was lost that way). The Dockerfiles copy projects one by one, so a new project fails the image build *after* CI is green — check both before bumping.

Since v1.3.0-alpha.2 the app ships as **one unified container** (`FullWorthHost__Unified=true`, web+backend+banking on :8080). The old split images (`fullworth-backend`/`-web`/`-banking`) are **no longer built** — anything still referencing them needs a unified migration. Note that the BFF still talks to the backend over loopback HTTP even when unified.

## Deploy

Deploy repo is `Juloc/docker` at `~/git/docker` (separate repo). FullWorth stacks: `fullworth/` (production, web.fullworth.de), `fullworth-demo/`, `fullworth-cloud/`, `fullworth-landing/` (also serves the apex). The `finance/` stack was retired on 2026-09-09. Bump the `FULLWORTH_VERSION` default in the compose files; the server may additionally pin it via its own `.env`.

`fullworth-platform-secrets` belongs to the **app stack alone** — the cloud stack shares nothing with it any more. It holds `data_encryption_key`: never `docker volume prune`, never `docker compose down -v` in a folder that mounts it. It is no longer `external`, so a fresh host needs no preparation; `DataEncryptionKeyGuard` refuses to start when the key cannot be the one this database was encrypted with.

## Conventions

- **Commit as `Juloc <juli.hiresch@gmail.com>`. Never credit Claude/AI — no `Co-Authored-By` trailer, no AI mention in messages.**
- The owner pushes to `main` extremely fast (60+ commits/hour). **Always `git fetch origin main` and rebase before pushing**, and re-check that a feature isn't already shipped before implementing it.
- Stage explicit paths, not `git add -A`, when a background job may be writing in the same repo.
- Keep commit messages conventional and explain the *why*.
- Git-bash/MSYS rewrites `/tmp/x`-style env values into `C:/...`; prefix with `MSYS_NO_PATHCONV=1` when passing unix-ish paths to containers.
- UI taste: clean, no clutter, no confusing routing/back-buttons, collapse crowded row actions into a `⋯` menu.
