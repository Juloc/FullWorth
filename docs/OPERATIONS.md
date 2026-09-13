# Operations

## Deploy

The canonical self-hosted deployment runs **two containers**:

- `fullworth` — Web, finance backend and banking modules in one ASP.NET process
- `fullworth-postgres` — PostgreSQL

The optional Codex / ChatGPT bridge is a third container only when the `codex` Compose profile is enabled.

Copy `.env.example` to `.env` and set the two required values:

```env
FULLWORTH_DOMAIN=finance.example.com
FULLWORTH_SECRET=use-a-long-random-secret-from-your-password-manager
```

Use a stable random secret of at least 32 characters. The normal deployment derives its database and
internal module credentials from this master secret. Existing installations can keep separate
credentials through the advanced compatibility overrides in `.env.example`.

The public reverse proxy must terminate HTTPS and forward only to `127.0.0.1:8098`. Passkey origin,
AllowedHosts and Enable Banking callback URLs are derived from `FULLWORTH_DOMAIN`.

```bash
docker compose config
docker compose pull
docker compose up -d
```

A fresh installation allows registration for exactly the first account. That account becomes the
instance administrator; public registration then closes automatically.

### Codex

Nothing to start. The Codex bridge is a process in the application container since
1.3.0-alpha.36, and it stays asleep until somebody signs in to Codex in the AI settings - an
installation that never does runs no Node process at all.

Upgrading from a stack that still has the `fullworth-codex` service, use `--remove-orphans`:

```bash
docker compose pull
docker compose up -d --remove-orphans
```

Without it the old sidecar keeps running, and two writers on one `CODEX_HOME` is the one reliable way
to lose the Codex sign-in. The `fullworth-codex` volume keeps its name and is mounted on the
application service now, so the sign-in itself survives the move.

## Passwords and keys

The admin menu shows the stored credentials and infrastructure secrets of this installation under
*Passwörter & Schlüssel*. Showing one requires a second confirmation — the code from your
authenticator app, or your account password when two-factor is off. The confirmation lasts five
minutes and ten reveals; the four that own the whole installation ask every time.

Every reveal is written to the audit table **and** to the container log:

```bash
docker compose logs fullworth | grep "Vault:"
```

Neither record contains the value, only which entry was asked for and by whom.

Account passwords, PINs and recovery codes are not there and cannot be: they are hashed with PBKDF2,
so nobody can display them — not an administrator and not the application.

## Enable Banking

Enable Banking is BYO per user by default. Each FullWorth user verifies and stores their own
Enable Banking application ID + RSA private key through the authenticated setup wizard. The key is
encrypted at rest and is never returned to the browser after setup.

A global Enable Banking key is legacy-only and is resolved only for pre-existing bank connections
without a user profile. It cannot create a new user bank connection.

Existing legacy deployments can either set:

- `ENABLE_BANKING_APPLICATION_ID`
- `ENABLE_BANKING_PRIVATE_KEY_BASE64`

or continue using the PEM compatibility override:

```bash
mkdir -p secrets
# place the existing key at:
# secrets/enable-banking-private-key.pem
docker compose -f docker-compose.yml -f docker-compose.enable-banking-legacy.yml up -d
```

## Backup and restore

Back up PostgreSQL, purchase files, Data Protection keys and `.env`. If the optional Codex bridge
is used, also back up its data volume.

```bash
ops/backup/backup-all.sh
ops/restore-test/verify-restore.sh
```

Configure offsite backups with a dedicated, narrow-scope account. Restore verification uses an
isolated database and should be run regularly. A live restore is destructive: create a fresh backup,
stop FullWorth, and use the restore scripts only with their explicit `--force` option.

## Secret rotation

Never commit a secret.

For the normal deployment, `FULLWORTH_SECRET` is intentionally stable because it also derives the
field-encryption key. Do not replace it in place while encrypted data exists. A rotation requires a
controlled re-encryption migration.

Existing deployments that still use separate compatibility credentials may rotate those paired
internal keys together. Verify health, login and a bank sync after rotation.

## Enable Banking private/restricted production

For personal testing, each FullWorth user should create their own Enable Banking Production
application, activate it by linking only their own accounts in the Enable Banking Control Panel, and
then add that application to FullWorth. Do not share one restricted application between unrelated
FullWorth users. Restricted production remains subject to Enable Banking's current terms and
linked-account rules.
