# Architecture

FullWorth is a self-hosted personal-finance platform. The public core is a small
set of ASP.NET (.NET 10) services behind a single web origin, backed by one
PostgreSQL database.

## Services

| Service | Image | Role |
|---|---|---|
| `fullworth-web` | `ghcr.io/juloc/fullworth-web` | Browser origin: auth (cookies, passkeys, recovery), the SPA shell, and a reverse proxy to the internal services. Owns the Identity/auth database. |
| `fullworth-backend` | `ghcr.io/juloc/fullworth-backend` | Domain core: accounts, transactions, categories, rules, budgets, purchases, contracts, portfolio/wealth, real estate, tax, analytics, coach, notifications, import/export and local Intelligence. |
| `fullworth-banking` | `ghcr.io/juloc/fullworth-banking` | Enable Banking sync (optional). Talks to the backend over an internal ingest key. |
| `fullworth-codex` | `ghcr.io/juloc/fullworth-codex` | Experimental GPT receipt-scan bridge. Off by default; least-privilege bridge key. |

Only `fullworth-web` is exposed. Backend and banking are reached through the web
proxy and internal keys; they are never published directly.

## Data model

- The **backend** owns the financial source-of-truth schema (accounts,
  transactions, categories, …) plus a **separate EF model + migration history**
  for Intelligence metadata (`__EFMigrationsHistory_Intelligence`) in the same
  database, so AI/cloud metadata never couples to financial records.
- The **web** service owns the Identity/auth schema (users, sessions, passkeys,
  recovery codes) in its own migration history.
- Migrations are hand-written raw SQL (see [MIGRATIONS.md](MIGRATIONS.md)).
- Multi-tenancy is modelled with **FullWorth Spaces** (`FullWorthSpaces`,
  `FullWorthSpaceMembers`, `FullWorthSpaceInvites`); rows are scoped by
  `FullWorthSpaceId`.

## Modules

Backend features live under `src/FullWorth.Backend/Modules/*` (Accounts,
Transactions, Categories, Rules, Budgets, Purchases, Amazon, Contracts,
Portfolio/Wealth, RealEstate, Tax, Analytics, Coach, Notifications, Ingestion,
Import, Parity and Intelligence). Web features live under
`src/FullWorth.Web/Modules/*` (Auth, Sessions, Passkeys, Pin, Recovery,
Bootstrap, Landing, Import, Purchases).

## Local Intelligence and the cloud client

Local Intelligence — merchant recognition, categorization, the rule engine,
suggestions, optional local AI, the local scheduler and local feedback capture
— is part of the open core and runs entirely on the self-hosted instance.

The instance also ships the **client** side of the optional FullWorth Cloud:
consent + connection state, a client-side contribution outbox, the
knowledge-pack consumer and signature verification. The central server is a
separate private product; see [CLOUD_INTEGRATION.md](CLOUD_INTEGRATION.md) and
[REPOSITORY_SEPARATION.md](REPOSITORY_SEPARATION.md). **The core runs fully
without the cloud.**

## Extension points

- `ILandingPageProvider` (web) lets a private deployment serve its own landing
  page for anonymous visitors without the public core depending on it. The core
  registers `DefaultLandingPageProvider` (redirect to sign-in).
- `GET /api/installation-state` returns the sanitized installation state
  (single/multi user, initialized, registration mode) for a landing/setup page.
  See [SELF_HOSTING.md](SELF_HOSTING.md).
