# Release

## Versioning

Use Semantic Versioning tags:

- Stable: `v1.4.0`
- Release candidate: `v1.4.0-rc.1`
- Other pre-release: `v1.4.0-alpha.1`

Only stable releases move `latest`, major and major/minor aliases.

## Verify

Before tagging:

```bash
dotnet restore FullWorth.slnx
dotnet build FullWorth.slnx --configuration Release --no-restore
dotnet test FullWorth.slnx --configuration Release --no-build
docker compose --env-file .env.example config --quiet
ops/restore-test/verify-restore.sh
```

Also review authentication, authorization, uploads, backups and live-bank checks relevant to the release.

## Images

A release publishes only the deployment images:

- `ghcr.io/juloc/fullworth` — unified Web + Backend + Banking host
- `ghcr.io/juloc/fullworth-codex` — optional Codex / ChatGPT bridge

Backend and Banking remain separate projects internally for module boundaries and tests, but they are not separate containers in the canonical deployment.

## Publish

```bash
git tag vX.Y.Z
git push origin vX.Y.Z
```

AMD64 and ARM64 start in parallel. AMD64 tags are available as soon as that architecture finishes; after ARM64 succeeds the normal tags become multi-architecture manifests.

## Tag policy

For stable `v1.4.0`:

- `1.4.0`
- `1.4`
- `1`
- `latest`
- `sha-<commit>`
- matching `-amd64` and `-arm64` tags

For pre-release `v1.4.0-alpha.1`:

- `1.4.0-alpha.1`
- `sha-<commit>`
- matching `-amd64` and `-arm64` tags

Pre-releases never update stable aliases.

## Deployment

Use one `FULLWORTH_VERSION` for both FullWorth and the optional Codex bridge. For reproducible production deployments, pin an exact version instead of `latest`.

See [Security architecture](SECURITY_ARCHITECTURE.md) and [Banking](BANKING.md).
