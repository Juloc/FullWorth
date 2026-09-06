# Release

## Verify

Before releasing, validate the complete solution and the deployment configuration:

```bash
dotnet build FullWorth.slnx --configuration Release
dotnet test FullWorth.slnx --configuration Release --no-build
docker compose --env-file .env.example config --quiet
ops/restore-test/verify-restore.sh
```

Review authentication, authorization, upload, backup and live-bank checks relevant to the release.
See [Security architecture](SECURITY_ARCHITECTURE.md) and [Live bank validation](LIVE_BANK_TEST_PLAN.md).

## Publish images

The release workflow publishes `fullworth-backend`, `fullworth-banking` and `fullworth-web` images to the
repository's GitHub Container Registry when a version tag is pushed.

```bash
git tag vX.Y.Z
git push origin vX.Y.Z
```

Use the exact version in `FULLWORTH_VERSION` when deploying published images. The compose stack uses
this single version for backend, banking, web and codex so a deployment cannot accidentally mix release
candidates. Pre-release tags publish only their exact version and commit-sha image tags.
