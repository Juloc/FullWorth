# Self-hosting

FullWorth is designed to run on a single host with Docker. The public core is
fully self-hostable and needs **neither FullWorth Cloud nor the demo overlay**.

## Quick start

The repo ships a `docker-compose.yml` that builds/pulls the four services and a
PostgreSQL database. A ready-made minimal stack (fixed internal secrets, only an
admin login to choose) lives in the separate `Juloc/docker` repository under
`fullworth/`.

Required for a production deployment (set via environment/secret):

| Variable | Purpose |
|---|---|
| `POSTGRES_PASSWORD` | database password (not `finance`/`fullworth`/`postgres`) |
| `FULLWORTH_DATA_ENCRYPTION_KEY` | base64 of exactly 32 bytes (AES-256 field cipher) |
| `FULLWORTH_BACKEND_INTERNAL_KEY` | web ↔ backend internal auth (≥ 16 chars) |
| `FULLWORTH_INGEST_KEY`, `FULLWORTH_BANKING_API_KEY` | banking ↔ backend auth |
| `FULLWORTH_PASSKEY_RP_ID`, `FULLWORTH_PASSKEY_ORIGIN`, `FULLWORTH_ALLOWED_HOSTS` | passkey relying-party + host binding |

Secrets are enforced only in `Production` and reject placeholder values.

## First user

Two idempotent paths coexist:

- **ENV bootstrap** — set `Bootstrap__Email` + `Bootstrap__Password` (and
  optionally `Bootstrap__DisplayName`, `Bootstrap__SpaceName`,
  `Bootstrap__BaseCurrency`). On startup, if no login exists, the first admin +
  owner FullWorth Space are created. Remove/rotate the values after first
  sign-in.
- **Interactive setup** — a landing/setup surface can read the sanitized state
  and create the first user. It never runs once a user exists.

Both are safe to leave configured together; whichever runs first wins and the
other becomes a no-op.

## Installation state and registration

`GET /api/installation-state` returns a sanitized view for the landing/setup
page — no user counts or identities:

```json
{ "mode": "singleUser", "initialized": true, "registration": "disabled" }
```

Configure via the `Installation` section:

- `Installation__Mode` = `SingleUser` (default) or `MultiUser`.
- `Installation__Registration` = `Open`, `InviteOnly` or `Disabled` (default).
  Only meaningful for `MultiUser`; a single-user install always reports
  `disabled`.

## Optional pieces

- **Live bank sync (Enable Banking)** — off unless you provide the application
  id, redirect URL and private key to `fullworth-banking`. Manual
  accounts/transactions work without it.
- **GPT receipt scan** — the experimental `fullworth-codex` bridge is off by
  default.
- **FullWorth Cloud** — optional; see [CLOUD_INTEGRATION.md](CLOUD_INTEGRATION.md).

## Reverse proxy / HTTPS

Terminate TLS at a reverse proxy and forward to `fullworth-web`. Set
`Passkeys__RelyingPartyId`, `Passkeys__Origins__0` (your https origin) and
`AllowedHosts`; the proxy address (or the Docker bridge network) is trusted via
`ReverseProxy__KnownProxies`/`KnownNetworks` so `X-Forwarded-Proto=https` is
honored.
