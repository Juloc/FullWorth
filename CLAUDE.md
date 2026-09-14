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

**That test server grows, and it has run out twice for different reasons.** Each test class clones the
migration template into its own database and a per-class DROP was measured as a real slowdown, so
clones are left behind. Both factories now drop leftovers older than six hours once per test process
(`PurgeAbandonedDatabases`); `TestDatabaseHygieneTests` insists that every factory which creates a
database also has one.

- **2026-09-11, 4 241 databases / 61 GB:** filled C:, which took the Docker engine down with it.
- **2026-09-14, 5 846 databases / 48 GB:** the Web factory had no purge at all, so a day of Web-only
  runs grew unchecked. The failure was **not** the disk — C: had 50 GB free — but
  `could not resize shared memory segment … No space left on device`: the container ran with Docker's
  default **64 MB `/dev/shm`**. Once shm is exhausted even `psql` cannot connect. It runs with
  `--shm-size=1g` now.

If it is ever huge again, count `pg_database` and check `df -h /dev/shm` **before** suspecting Docker
or the disk — the error message points at the wrong thing. Recreating the container must keep its five
tuning flags, or every run afterwards is slower for no visible reason:

```bash
docker run -d --name fullworth-ci-pg --shm-size=1g -p 5432:5432 \
  -v fullworth-ci-pg-data:/var/lib/postgresql \
  -e POSTGRES_USER=fullworth_test -e POSTGRES_PASSWORD=fullworth_test_password -e POSTGRES_DB=fullworth_test \
  postgres:18 \
  -c max_connections=500 -c fsync=off -c synchronous_commit=off -c full_page_writes=off -c shared_buffers=256MB
```

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
- Every stylesheet is under `styles/` or belongs to a page. The order `index.html` loads them in:
  `tokens` → `reset` → `appearance` → `shell` → `components` → `app` → `responsive` → `design-depth`
  → `dialogs`, then every page's own `page.css`, then `styles/mobile-polish.css` last.
  `styles/features/` is gone and so is the `wwwroot` root. `styles/app.css` was the collecting bucket —
  1 056 rules, 791 of them for exactly one page; those live with their page now. What stayed is what is
  genuinely shared, plus the rules a later sheet overrides: moved to a `page.css` they would win where
  they used to lose. **Tokens only** — no hardcoded colours, no frameworks, no DOM hacks.
- **Moving CSS between files is only safe if you measure it.** A rule that moves to a `page.css` moves
  later in the cascade, past `responsive`, `design-depth` and `dialogs`. Two checks, both cheap:
  compare the rules the browser parses (`document.styleSheets` walked recursively) before and after —
  the multiset must be identical — and compare `getComputedStyle` for every element of every view.
  Use a full reload for the second one: swapping `<link>` elements re-declares `@font-face`, and with
  `font-display: optional` the font then falls back, which shows up as text-width noise everywhere.
- `components/` knows neither a page nor the server. `features/` may. `core/` is the system layer and
  knows nothing visual.
- The CSP allows `style-src-attr 'unsafe-inline'`, so a style *attribute* works — but prefer tokens and
  classes anyway. A `<style>` block is blocked, and so is an `onclick="…"`: `script-src 'self'` covers
  `script-src-attr`. That holds for markup written in a JavaScript template string or a C# raw string
  just as much as in a `.html` file — two such places lived in the tree for months because the guard
  only read `.html`. `SecurityHeadersSourceAuditTests` reads all three now.
- **Buttons: use the shared module roles only** — `.btn` + `.btn-primary` / `.btn-secondary` / `.btn-danger` (`styles/components.css`). Do not hand-roll button styling. (`.primary-action` in `shell.css` is the older variant still used app-wide.)
- **Alles Markup liegt in `wwwroot`.** Die drei Import-Seiten hielten ihr HTML einmal in C#-Rohstringen, und `ops/ui-harness` las sie von dort; seit sie unter `pages/settings/import/` liegen, ist beides weg. Eigenständige Dokumente gibt es noch zwei, beide mit Grund: `auth/` (dort gibt es noch keine Sitzung) und `account-deletion/` (dort ist das Konto abgeschaltet, ein Menü führte ins Leere).

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
