# FullWorth

[![Release](https://img.shields.io/github/v/release/Juloc/FullWorth?include_prereleases&sort=semver)](https://github.com/Juloc/FullWorth/releases)
[![Release images](https://github.com/Juloc/FullWorth/actions/workflows/release.yml/badge.svg)](https://github.com/Juloc/FullWorth/actions/workflows/release.yml)
[![Docker](https://img.shields.io/badge/Docker-amd64%20%7C%20arm64-2496ED?logo=docker&logoColor=white)](https://github.com/Juloc/FullWorth/pkgs/container/fullworth)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)
![PostgreSQL 18](https://img.shields.io/badge/PostgreSQL-18-4169E1?logo=postgresql&logoColor=white)
![PWA](https://img.shields.io/badge/PWA-installable-5A0FC8?logo=pwa&logoColor=white)
![Self-hosted](https://img.shields.io/badge/self--hosted-yes-2ea44f)

FullWorth is a self-hosted personal finance app for accounts, transactions, budgets, contracts, purchases, investments and optional automatic bank synchronization.

## Docker setup

The normal FullWorth stack is only **2 containers**:

- `fullworth` — Web, finance backend and banking in one application
- `fullworth-postgres` — PostgreSQL

Personal AI through a ChatGPT/Codex sign-in is included in the application container — no third
service and nothing to enable.

You need:

- Docker + Docker Compose
- a domain such as `finance.example.com`
- HTTPS through Caddy, Traefik or another reverse proxy

Download `docker-compose.yml` and start it. There is nothing to fill in:

```bash
docker compose up -d
```

No `.env`, no secret to invent, no domain to declare. Every secret this installation needs is created
by the service that reads it, the first time it starts without one — a separate value per purpose,
none of which ever leaves your server. The address you reach FullWorth at is learned from your first
registration, and the passkey relying party, the passkey origin, the Enable Banking redirect and the
host pin all follow from it.

FullWorth listens on `127.0.0.1:8080` by default. `.env.example` lists what you *can* change.

> **Back up the `fullworth-secrets` volume.** It holds `data_encryption_key`, and without it every
> encrypted column in the database is unreadable — a PostgreSQL backup alone cannot bring it back,
> because what was lost never lived in PostgreSQL. Never run `docker compose down -v` in this folder.

### Caddy example

```caddy
finance.example.com {
    reverse_proxy 127.0.0.1:8080
}
```

Open `https://finance.example.com`.

On a fresh installation, registration is available for the **first account only**. That account becomes the instance administrator. After it is created, public registration closes automatically.

## Codex / ChatGPT

Nothing to start. The bridge is part of the application container, and it only starts a process once
you actually sign in under *Settings → AI access* — an installation that never does runs nothing
extra. Your sign-in lives in the `fullworth-codex` volume and belongs to a separate user inside the
container, which the application itself cannot read.

FullWorth works normally without it, with your own API key or your own provider.

## Enable Banking

Bank access is optional. FullWorth also works with manual accounts and imports.

During the first-login setup, FullWorth explains Enable Banking and lets each user configure their own banking access.

FullWorth can:

- create the Enable Banking application automatically (Alpha), or
- use an Application ID + private key created manually in the Enable Banking Control Panel.

For private self-hosting, every user should use their own Enable Banking account/application for their own accounts.

The callback URL is created automatically from the address your installation learned:

```text
https://finance.example.com/connect/enable-banking/callback
```

## Updates

Update the normal 2-container stack with:

```bash
docker compose pull
docker compose up -d --remove-orphans
```

`--remove-orphans` matters once, when upgrading from a version older than 1.3.0-alpha.36: the Codex
bridge used to be its own container, and leaving the old one running means two processes writing one
Codex sign-in.

To stay on a specific version:

```env
FULLWORTH_VERSION=1.4.0
```

Normal version tags automatically use the correct architecture on both **AMD64** and **ARM64**.

## Backup

Always back up:

- `fullworth-secrets` — **the one that cannot be recreated.** It holds `data_encryption_key`; without
  it every encrypted column in the database is unreadable, and no PostgreSQL backup helps
- `fullworth-data` — the database
- `fullworth-purchases` — receipt files
- `fullworth-pension` — uploaded pension statements
- `fullworth-dataprotection` — the key ring that signs your sign-in cookies

If you sign in to Codex, also back up `fullworth-codex` — it holds that sign-in.

A backup of the database alone is not a backup. `fullworth-secrets` and `fullworth-data` belong
together: either without the other is unreadable.

## License

FullWorth is source-available proprietary software. Personal, non-commercial self-hosting and modification are permitted under the [FullWorth Proprietary License](LICENSE).
