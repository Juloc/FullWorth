# Release

## Versioning

Use Semantic Versioning tags:

- Stable: `v1.4.0`
- Release candidate: `v1.4.0-rc.1`
- Other pre-release: `v1.4.0-beta.1`

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

## Publish

```bash
git tag vX.Y.Z
git push origin vX.Y.Z
```

A tag starts both architecture builds at the same time.

### AMD64 path

AMD64 publishes the normal tags immediately. The GitHub Release depends only on AMD64, so ARM64 cannot delay normal testing or release availability.

### ARM64 path

ARM64 runs separately through QEMU and publishes `*-arm64` tags. AMD64 also keeps `*-amd64` tags.

After both architectures finish successfully, the workflow replaces the normal tags with multi-architecture manifests.

## Tag policy

For stable `v1.4.0`:

- `1.4.0`
- `1.4`
- `1`
- `latest`
- `sha-<commit>`
- matching `-amd64` and `-arm64` tags

For pre-release `v1.4.0-rc.1`:

- `1.4.0-rc.1`
- `sha-<commit>`
- matching `-amd64` and `-arm64` tags

Pre-releases never update stable aliases.

## Deployment

Use one `FULLWORTH_VERSION` for web, backend, banking and codex. For reproducible production deployments, pin an exact version instead of `latest`.

See [Security architecture](SECURITY_ARCHITECTURE.md) and [Live bank validation](LIVE_BANK_TEST_PLAN.md).
