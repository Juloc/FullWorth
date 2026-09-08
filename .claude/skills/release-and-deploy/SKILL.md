---
name: release-and-deploy
description: Cut a new FullWorth alpha release and roll it out to the Docker stacks. Use when asked to release, publish a version, bump the app version, or deploy/update the beta, apex, demo or cloud stack.
---

# Release & deploy FullWorth

Two separate repos are involved: the app (`Juloc/FullWorth`, here) and the deploy repo (`Juloc/docker` at `~/git/docker`).

## 0. Check that `main` is actually green first

`ci.yml` is **`workflow_dispatch` only** — it is not a tag gate and nothing runs it on push, so
`main` can sit red for a long time without anyone noticing. A release does not run the test suite
either. Run it and read the result before requesting a release:

```bash
gh workflow run ci.yml --ref main
gh run list --workflow=ci.yml --limit 1 --json databaseId --jq '.[0].databaseId'
gh run view <RUN_ID> --json conclusion,jobs --jq '.conclusion, (.jobs[] | "\(.name) -> \(.conclusion)")'
```

When something fails, find out whether it is yours before touching it: create a worktree at the
commit before your work (`git worktree add --detach <path> <sha>`) and run the same filter there.
Pre-existing red tests are common and are often stale string guards rather than product bugs — fix
them to assert the invariant they meant, never weaken them. Report timeouts separately: the local
Docker Postgres throws `Npgsql … operation has timed out` under load, which is environmental.

## 1. Release the app

```bash
git fetch origin main && git rebase origin/main      # owner pushes fast — always sync first
printf 'v1.3.0-alpha.N\n' > ops/release-request.txt  # bump N
git add ops/release-request.txt && git commit -m "release: request v1.3.0-alpha.N" && git push origin main
```

Pushing `ops/release-request.txt` triggers `Alpha Release Request` (creates the tag), which triggers `Release` (builds + publishes). Watch it:

```bash
gh run list --limit 4
gh run view <RUN_ID> --json status,conclusion
```

Then **verify the images actually exist** before deploying:

```bash
docker manifest inspect ghcr.io/juloc/fullworth:1.3.0-alpha.N >/dev/null && echo PUBLISHED
docker manifest inspect ghcr.io/juloc/fullworth-codex:1.3.0-alpha.N >/dev/null && echo PUBLISHED
```

Never bump a deploy stack to a tag you have not verified exists.

## 2. Roll out to the Docker stacks

```bash
cd ~/git/docker && git fetch origin -q            # separate repo, also sync first
```

Stacks and what they are:

| Stack | Purpose | Image |
|---|---|---|
| `fullworth/` | beta, web.fullworth.de | unified `ghcr.io/juloc/fullworth` |
| `finance/` | apex, fullworth.de | unified |
| `fullworth-demo/` | public demo, per-visitor sessions | compose is migrated to unified, **but the running containers are still `fullworth-web`/`-backend:1.2.0-rc.9`** |
| `fullworth-cloud/` | private cloud API/worker | own `fullworth-cloud*` images |
| `fullworth-landing/` | landing page | own image |

Bump the `FULLWORTH_VERSION` default (it appears on both the `fullworth` and `fullworth-codex` image lines):

```bash
sed -i 's/1\.3\.0-alpha\.OLD/1.3.0-alpha.NEW/g' fullworth/docker-compose.yml finance/docker-compose.yml
git diff --stat && git add -A && git commit -m "fullworth,finance: bump default image to 1.3.0-alpha.NEW" && git push origin main
```

## 3. Tell the owner what still needs doing on the server

The compose default is only used when the server does not pin the version itself. Always state this:

> If the server pins `FULLWORTH_VERSION` in its own `.env`, set it to the new version and run
> `docker compose pull && docker compose up -d`.

## Gotchas

- Since v1.3.0-alpha.2 the app is **one unified container** (`FullWorthHost__Unified=true`, :8080). The split `fullworth-backend`/`-web`/`-banking` images are no longer built, so a stack on those tags cannot simply be re-tagged — it needs a unified migration (collapse backend+web into one service, keep network aliases so siblings still resolve it).
- The unified image and the old split images use **different secret conventions** (master-secret entrypoint vs per-secret `*_FILE`). Check before migrating a stack that relies on `*_FILE`.
- A stack's compose file being migrated does **not** mean the running containers were. Compare `docker ps --format '{{.Names}}\t{{.Image}}'` against the compose file before assuming a stack is current. Bringing the demo onto the unified image means a schema migration across many versions on live public data — dump the volume first and let the owner trigger it.
- A release only contains commits that were on `main` **before** the tag was created. If you fix something after requesting a release, it needs the next alpha.
- **A failed release burns its tag.** `Alpha Release Request` creates the tag first, so if `Release` then fails, that version is spent — fix the cause and request the *next* N rather than retrying the same one. (`v1.3.0-alpha.11` was lost this way.)
- If a release fails inside `dotnet publish` with an `IOException` on a file under some project's `obj/` or `bin/` (`GenerateDepsFile`, `DefineStaticWebAssets`, …), it is a duplicate-project-instance race, not flakiness: MSBuild keys project instances by their global properties, so a `ProjectReference` carrying `AdditionalProperties` builds that project (and everything below it) a second time into the same output directories. Find the property and check whether anything still reads it — do not paper over it by serializing the build.
- Commit as `Juloc <juli.hiresch@gmail.com>`; never credit Claude.
