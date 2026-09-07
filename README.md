# FullWorth

FullWorth is a self-hosted personal finance application for accounts, transactions, budgets, contracts, purchases, investments and optional bank connections.

## Quick start

Requirements:

- Docker + Docker Compose
- an HTTPS reverse proxy such as Caddy
- a public hostname, for example `finance.example.com`

Create a secure `.env`:

```bash
sh scripts/setup-env.sh finance.example.com you@example.com
```

The script generates all internal secrets plus a random first-admin password. Then start FullWorth:

```bash
docker compose pull
docker compose up -d
```

After the first successful sign-in, remove `FULLWORTH_BOOTSTRAP_EMAIL` and `FULLWORTH_BOOTSTRAP_PASSWORD` from `.env`.

Example Caddy route:

```caddy
finance.example.com {
    reverse_proxy 127.0.0.1:8098
}
```

Only `.env.example` and `docker-compose.yml` are used as the canonical deployment examples. Optional and advanced settings stay commented in the same `.env.example`.

## Enable Banking

Bank access is optional. FullWorth can be used with manual accounts and imports without Enable Banking.

For automatic bank synchronization, each self-hosted user configures their own Enable Banking account/application from the first-login setup or later in Settings. FullWorth can create the application automatically (Beta), or the user can provide an Application ID and private key manually.

The public callback URL is derived automatically from `FULLWORTH_DOMAIN`:

```text
https://finance.example.com/connect/enable-banking/callback
```

Legacy global Enable Banking credentials remain supported only for existing older installations.

## Docker image tags

Images are published to GHCR as:

- `ghcr.io/juloc/fullworth-web`
- `ghcr.io/juloc/fullworth-backend`
- `ghcr.io/juloc/fullworth-banking`
- `ghcr.io/juloc/fullworth-codex`

Stable releases publish:

- `1.4.0` — exact release
- `1.4` — latest stable release in that minor line
- `1` — latest stable release in that major line
- `latest` — latest stable release
- `sha-abcdef0` — exact commit build

Pre-releases such as `1.4.0-rc.1` publish only the exact version and commit tags, so they never move `latest`, `1` or `1.4`.

Architecture-specific tags are also available directly:

- `1.4.0-amd64`
- `1.4.0-arm64`

Normal release tags are published for AMD64 first. ARM64 builds run independently in parallel. When both architectures are ready, the normal tags are upgraded to multi-architecture manifests so Docker automatically pulls the correct image.

## Releases

Push a SemVer tag to publish a release:

```bash
git tag v1.4.0
git push origin v1.4.0
```

Release candidates use tags such as:

```bash
git tag v1.4.0-rc.1
git push origin v1.4.0-rc.1
```

The release workflow publishes the images and creates the matching GitHub Release with generated release notes. See [docs/RELEASE.md](docs/RELEASE.md) for the release checklist.

## Development

```bash
dotnet restore FullWorth.slnx
dotnet build FullWorth.slnx --configuration Release
dotnet test FullWorth.slnx --configuration Release --no-build
```

## Documentation

Additional deployment, backup, security and product documentation is available in [docs](docs/).

## License

FullWorth is source-available proprietary software. Personal, non-commercial self-hosting and modification are permitted under the [FullWorth Proprietary License](LICENSE). Redistribution, commercial use, hosted use for third parties, and use in competing products require prior written permission.
