# FullWorth

[![Release](https://img.shields.io/github/v/release/Juloc/FullWorth?include_prereleases&sort=semver)](https://github.com/Juloc/FullWorth/releases)
[![Release images](https://github.com/Juloc/FullWorth/actions/workflows/release.yml/badge.svg)](https://github.com/Juloc/FullWorth/actions/workflows/release.yml)
[![Docker](https://img.shields.io/badge/Docker-amd64%20%7C%20arm64-2496ED?logo=docker&logoColor=white)](https://github.com/Juloc/FullWorth/pkgs/container/fullworth-web)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)
![PostgreSQL 18](https://img.shields.io/badge/PostgreSQL-18-4169E1?logo=postgresql&logoColor=white)
![PWA](https://img.shields.io/badge/PWA-installable-5A0FC8?logo=pwa&logoColor=white)
![Self-hosted](https://img.shields.io/badge/self--hosted-yes-2ea44f)

FullWorth is a self-hosted personal finance app for accounts, transactions, budgets, contracts, purchases, investments and optional automatic bank synchronization.

## Docker setup

You need:

- Docker + Docker Compose
- a domain such as `finance.example.com`
- HTTPS through Caddy, Traefik or another reverse proxy

Download `docker-compose.yml` and copy `.env.example` to `.env`.

For a normal installation you only need to fill in:

```env
FULLWORTH_DOMAIN=finance.example.com
FULLWORTH_SECRET=use-a-long-random-secret-from-your-password-manager
```

Keep `FULLWORTH_SECRET` safe and stable. It protects the database connection, internal services and encrypted FullWorth data.

Start FullWorth:

```bash
docker compose pull
docker compose up -d
```

FullWorth listens on `127.0.0.1:8098` by default.

### Caddy example

```caddy
finance.example.com {
    reverse_proxy 127.0.0.1:8098
}
```

Open `https://finance.example.com`.

On a fresh installation, registration is available for the **first account only**. That account becomes the instance administrator. After it is created, public registration closes automatically.

## Enable Banking

Bank access is optional. FullWorth also works with manual accounts and imports.

During the first-login setup, FullWorth explains Enable Banking and lets each user configure their own banking access.

FullWorth can:

- create the Enable Banking application automatically (Beta), or
- use an Application ID + private key created manually in the Enable Banking Control Panel.

For private self-hosting, every user should use their own Enable Banking account/application for their own accounts.

The callback URL is created automatically from `FULLWORTH_DOMAIN`:

```text
https://finance.example.com/connect/enable-banking/callback
```

## Updates

To update to the newest stable images:

```bash
docker compose pull
docker compose up -d
```

To stay on a specific version, set for example:

```env
FULLWORTH_VERSION=1.4.0
```

Normal version tags automatically use the correct architecture on both **AMD64** and **ARM64**.

## Backup

Back up these Docker volumes:

- `fullworth-postgres-data`
- `fullworth-purchases-data`
- `fullworth-web-dataprotection`
- `fullworth-codex-data`

Also back up your `.env`. Losing `FULLWORTH_SECRET` can make encrypted data inaccessible.

## License

FullWorth is source-available proprietary software. Personal, non-commercial self-hosting and modification are permitted under the [FullWorth Proprietary License](LICENSE).
