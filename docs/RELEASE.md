# Release

## Versioning

Semantic Versioning tags. While FullWorth is alpha the major version is `0`, so releases are `v0.80.0`, `v0.81.0`, ...
(a feature release raises the minor number, a fix on top of one the patch number). `1.0.0` is for the day the data model and the
upgrade path are meant to stay.

- Stable: `v0.80.0`, later `v1.4.0` - moves `latest`
- Release candidate: `v0.81.0-rc.1`
- Other pre-release: `v0.81.0-alpha.1`

Only stable releases move `latest`, the major and the major/minor aliases. The older `v1.3.0-alpha.N` tags (up to
`v1.3.0-alpha.79`) stay in GHCR and on GitHub as pre-releases.

## Request a release

1. Write the notes for this release into `ops/release-notes.md`. Its first line is `# 0.80.0` (the version); it is the text
   of the GitHub release, so write it for whoever updates: what is new, what to know before updating, what is not tested.
2. Put the tag into `ops/release-request.txt`:

   ```bash
   printf 'v0.80.0\n' > ops/release-request.txt
   git add ops/release-request.txt ops/release-notes.md
   git commit -m "release: request v0.80.0" && git push origin main
   ```

3. The `Release Request` workflow checks the tag and (for a stable one) that the notes start with the same version - **before**
   it creates the tag, because a failed release burns its tag. Then `Release` builds and publishes the image and creates the
   GitHub release with the notes and the "Latest" mark (a pre-release gets neither).

Without notes file or with notes of another version nothing is tagged; fix the file and push again.

## Verify

Before tagging:

```bash
dotnet restore FullWorth.slnx
dotnet build FullWorth.slnx --configuration Release --no-restore
dotnet test FullWorth.slnx --configuration Release --no-build
docker compose config --quiet
ops/restore-test/verify-restore.sh
```

Also review authentication, authorization, uploads, backups and live-bank checks relevant to the release.

## Images

A release publishes one deployment image:

- `ghcr.io/juloc/fullworth` — unified Web + Backend + Banking host, with the Codex bridge as a
  second process under its own user

`ghcr.io/juloc/fullworth-codex` is no longer built. Tags up to 1.3.0-alpha.35 stay in GHCR, so a
deployment pinned to one of those keeps working; from alpha.36 the sidecar service is removed from the
compose files instead (`--remove-orphans` on the upgrade, or two writers share one `CODEX_HOME`).

Backend and Banking remain separate projects internally for module boundaries and tests, but they are not separate containers in the canonical deployment.

## Publish

Normally through the request above. Pushing a `v*` tag by hand also starts `Release`, but then the notes come from
`ops/release-notes.md` only if it is headed by that version, otherwise GitHub generates them from the merged pull requests -
which for direct pushes to `main` is close to nothing.

AMD64 and ARM64 start in parallel. AMD64 tags are available as soon as that architecture finishes; after ARM64 succeeds the normal tags become multi-architecture manifests.

## Tag policy

For stable `v0.80.0`:

- `0.80.0`
- `0.80`
- `0`
- `latest`
- `sha-<commit>`
- matching `-amd64` and `-arm64` tags

For pre-release `v0.81.0-rc.1`:

- `0.81.0-rc.1`
- `sha-<commit>`
- matching `-amd64` and `-arm64` tags

Pre-releases never update stable aliases.

## Deployment

Use one `FULLWORTH_VERSION` for both FullWorth and the optional Codex bridge. For reproducible production deployments, pin an exact version instead of `latest`.

See [Security architecture](SECURITY_ARCHITECTURE.md) and [Banking](BANKING.md).
