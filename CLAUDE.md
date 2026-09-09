# FullWorth

Self-hosted personal finance platform: .NET 10 backend + **vanilla-JS** frontend (no framework, no build step). Public repo `Juloc/FullWorth`. German UI.

System-wide context across all FullWorth repos: `../CLAUDE.md`. The single active work list:
[docs/IMPROVEMENT_PLAN.md](docs/IMPROVEMENT_PLAN.md).

## Repo layout

- `src/FullWorth.Backend` — API + all domain modules (`Modules/<Area>/…`); owns `FullWorthDbContext` and `IntelligenceDbContext`
- `src/FullWorth.Web` — auth/shell host **and the BFF**; owns ASP.NET Identity (schema `auth`), passkeys, PIN, sessions. The frontend lives in `src/FullWorth.Web/wwwroot`
- `src/FullWorth.Banking`, `src/FullWorth.FinTs` — bank connectivity
- `src/FullWorth.Compensation.Core` — the German payroll engine, **deliberately dependency-free** so it also compiles to WASM
- `src/FullWorth.Compensation.Wasm` — browser bundle of that engine for the landing page; **not in `FullWorth.slnx` and not in any Dockerfile** (needs the `wasm-tools` workload)
- `src/Shared/SecretBootstrap.cs` — not a project; `<Compile Include>`-linked into Backend, Web and Banking. Docker-secret file config source + fail-closed `RequireSecret`
- `src/FullWorth.CodexBridge` — Node sidecar (`server.mjs`) that runs `codex exec`; exposes `POST /execute` (`{systemInstruction, inputJson, jsonSchema, model}` → `{success, outputJson}`), `POST /scan`, `GET /status`, `POST /auth/start`, `GET /auth/{id}`, `POST /logout`, `GET /models`, `GET /logs/recent`. Auth: `X-FullWorth-Internal-Key` + `X-FullWorth-Codex-Scope` (64-hex sha256)
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

There is **no linter, no formatter and no automated browser/e2e test** in this repo. `ci.yml` is `workflow_dispatch` only — it is **not** a tag gate and does not run on push, so `main` can be red unnoticed. Run it before requesting a release.

## Frontend rules

- Vanilla ES modules in `wwwroot`, **no build step**. Syntax-check with:
  `cp file.js /tmp/c.mjs && node --check /tmp/c.mjs`
- CSS is a 5-layer scheme in `wwwroot/styles/` (`tokens` → `reset` → `shell` → `components` → `responsive`) plus `styles/features/<feature>.css`. **Tokens only** — no hardcoded colors, no frameworks, no DOM hacks. Six stylesheets still sit at the `wwwroot` root outside that scheme (`app.css` is the largest); do not add a seventh.
- The CSP allows `style-src-attr 'unsafe-inline'`, so a style *attribute* works — but prefer tokens and classes anyway. A `<style>` block is blocked.
- **Buttons: use the shared module roles only** — `.btn` + `.btn-primary` / `.btn-secondary` / `.btn-danger` (`styles/components.css`). Do not hand-roll button styling. (`.primary-action` in `shell.css` is the older variant still used app-wide.)
- Don't create `features/<name>/` subfolders; keep feature files flat in `features/`.
- **Not all markup is in `wwwroot`:** `Modules/Import/{ImportCenter,FinanzguruImport,BrokerPdfImport}Page.cs` keep their HTML in C# raw string literals. `ops/ui-harness` parses those literals so an edited inlined page is served edited.

### Live UI verification (use this!)
Two ways, neither needs credentials:

- `node ops/ui-harness/server.mjs` → <http://127.0.0.1:8095> serves the real `wwwroot` against fixtures. Use this to measure layout and open dialogs.
- The `fullworth-dev` stack (`~/git/docker/fullworth-dev`) serves <http://127.0.0.1:8098> and **bind-mounts `src/FullWorth.Web/wwwroot`**, so frontend edits are live immediately — no rebuild, no redeploy.

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
2. `Alpha Release Request` workflow creates the tag → `Release` workflow publishes `ghcr.io/juloc/fullworth` and `ghcr.io/juloc/fullworth-codex`, amd64 + arm64 (~4–16 min).
3. Verify: `gh run list`, then `docker manifest inspect ghcr.io/juloc/fullworth:1.3.0-alpha.N`.

**A failed release burns its tag** (alpha.11 was lost that way). The Dockerfiles copy projects one by one, so a new project fails the image build *after* CI is green — check both before bumping.

Since v1.3.0-alpha.2 the app ships as **one unified container** (`FullWorthHost__Unified=true`, web+backend+banking on :8080). The old split images (`fullworth-backend`/`-web`/`-banking`) are **no longer built** — anything still referencing them needs a unified migration. Note that the BFF still talks to the backend over loopback HTTP even when unified.

## Deploy

Deploy repo is `Juloc/docker` at `~/git/docker` (separate repo). FullWorth stacks: `fullworth/` (production, web.fullworth.de), `fullworth-demo/`, `fullworth-cloud/`, `fullworth-dev/` (local), `fullworth-landing/` (also serves the apex). The `finance/` stack was retired on 2026-09-09. Bump the `FULLWORTH_VERSION` default in the compose files; the server may additionally pin it via its own `.env`.

`fullworth-platform-secrets` is an **external** volume shared by the app and cloud stacks and holds `data_encryption_key` — never `docker volume prune`, never `docker compose down -v` in a folder that mounts it.

## Conventions

- **Commit as `Juloc <juli.hiresch@gmail.com>`. Never credit Claude/AI — no `Co-Authored-By` trailer, no AI mention in messages.**
- The owner pushes to `main` extremely fast (60+ commits/hour). **Always `git fetch origin main` and rebase before pushing**, and re-check that a feature isn't already shipped before implementing it.
- Stage explicit paths, not `git add -A`, when a background job may be writing in the same repo.
- Keep commit messages conventional and explain the *why*.
- Git-bash/MSYS rewrites `/tmp/x`-style env values into `C:/...`; prefix with `MSYS_NO_PATHCONV=1` when passing unix-ish paths to containers.
- UI taste: clean, no clutter, no confusing routing/back-buttons, collapse crowded row actions into a `⋯` menu.
