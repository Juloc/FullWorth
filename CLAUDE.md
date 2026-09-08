# FullWorth

Self-hosted personal finance platform: .NET 10 backend + **vanilla-JS** frontend (no framework, no build step). Public repo `Juloc/FullWorth`. German UI.

## Repo layout

- `src/FullWorth.Backend` — API + all domain modules (`Modules/<Area>/…`)
- `src/FullWorth.Web` — auth/shell host; the frontend lives in `src/FullWorth.Web/wwwroot`
- `src/FullWorth.Banking`, `src/FullWorth.FinTs` — bank connectivity
- `src/FullWorth.CodexBridge` — Node sidecar (`server.mjs`) that runs `codex exec`; exposes `POST /execute` (`{systemInstruction, inputJson, jsonSchema, model}` → `{success, outputJson}`), `POST /scan`, `GET /status`, `GET /models`. Auth: `X-FullWorth-Internal-Key` + `X-FullWorth-Codex-Scope` (64-hex sha256).
- `tests/FullWorth.Backend.Tests`, `tests/FullWorth.Web.Tests`
- `ops/release-request.txt` — release trigger (see below)

## Build & test

```bash
dotnet build src/FullWorth.Backend/FullWorth.Backend.csproj -c Debug -v q --nologo
dotnet test tests/FullWorth.Backend.Tests/FullWorth.Backend.Tests.csproj --nologo --filter "FullyQualifiedName~SomeTests"
```

**Gotcha:** integration tests need `FULLWORTH_TEST_POSTGRES` pointing at an isolated Postgres 18. Without it they fail with `FULLWORTH_TEST_POSTGRES must point to the isolated PostgreSQL test server` — that is **environmental, not a regression**. Pure unit tests (calculators, parsers) run fine without it. A `fullworth-ci-pg` container usually listens on :5432.

Always filter to the relevant tests; the full suite is large and local Docker struggles under it.

## Frontend rules

- Vanilla ES modules in `wwwroot`, **no build step**. Syntax-check with:
  `cp file.js /tmp/c.mjs && node --check /tmp/c.mjs`
- CSS is a 5-layer scheme in `wwwroot/styles/` (`tokens` → `reset` → `shell` → `components` → `responsive`) plus `styles/features/<feature>.css`. **Tokens only** — no hardcoded colors, no inline styles (CSP blocks them), no frameworks, no DOM hacks.
- **Buttons: use the shared module roles only** — `.btn` + `.btn-primary` / `.btn-secondary` / `.btn-danger` (`styles/components.css`). Do not hand-roll button styling. (`.primary-action` in `shell.css` is the older variant still used app-wide.)
- Don't create `features/<name>/` subfolders; keep feature files flat in `features/`.

### Live UI verification (use this!)
The `fullworth-test` container serves <http://localhost:8099> and **bind-mounts `src/FullWorth.Web/wwwroot`**, so frontend edits are live immediately — no rebuild, no redeploy. Load the page and check the browser console for module errors after any frontend refactor.

### Compensation ("Gehalt") feature
`features/compensation-shared.js` is the **single source of truth** for the profile↔form mapping (`readProfile` / `fillProfile`), car-factor derivation, benefit rows, formatters and api/notify. The calculator, history and extended views all import from it — **never re-implement these per view** (they used to be triplicated and silently diverged).

## Release

1. Bump `ops/release-request.txt` to `vX.Y.Z-alpha.N`, commit, push to `main`.
2. `Alpha Release Request` workflow creates the tag → `Release` workflow publishes `ghcr.io/juloc/fullworth` and `ghcr.io/juloc/fullworth-codex` (~4–16 min).
3. Verify: `gh run list`, then `docker manifest inspect ghcr.io/juloc/fullworth:1.3.0-alpha.N`.

Since v1.3.0-alpha.2 the app ships as **one unified container** (`FullWorthHost__Unified=true`, web+backend+banking on :8080). The old split images (`fullworth-backend`/`-web`/`-banking`) are **no longer built** — anything still referencing them needs a unified migration.

## Deploy

Deploy repo is `Juloc/docker` at `~/git/docker` (separate repo). Stacks: `fullworth/` (beta, web.fullworth.de), `finance/` (apex), `fullworth-demo/`, `fullworth-cloud/`, `fullworth-landing/`. Bump the `FULLWORTH_VERSION` default in the compose files; the server may additionally pin it via its own `.env`.

## Conventions

- **Commit as `Juloc <juli.hiresch@gmail.com>`. Never credit Claude/AI — no `Co-Authored-By` trailer, no AI mention in messages.**
- The owner pushes to `main` extremely fast (60+ commits/hour). **Always `git fetch origin main` and rebase before pushing**, and re-check that a feature isn't already shipped before implementing it.
- Keep commit messages conventional and explain the *why*.
- Git-bash/MSYS rewrites `/tmp/x`-style env values into `C:/...`; prefix with `MSYS_NO_PATHCONV=1` when passing unix-ish paths to containers.
- UI taste: clean, no clutter, no confusing routing/back-buttons, collapse crowded row actions into a `⋯` menu.
