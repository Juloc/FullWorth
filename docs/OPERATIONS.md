# Operations

## Deploy

FullWorth is designed for a reverse proxy that terminates HTTPS and forwards only to FullWorth.Web.
Copy `.env.example` to `.env`, replace every placeholder with a unique value and set the public
hostname in `FULLWORTH_ALLOWED_HOSTS`, `FULLWORTH_PASSKEY_RP_ID` and `FULLWORTH_PASSKEY_ORIGIN`.

Required secrets include the database password, the three service credentials and the base64-encoded
32-byte data-encryption key. Enable Banking itself is BYO by default: set the instance-wide
`ENABLE_BANKING_REDIRECT_URL`, then let each FullWorth user verify and store their own Enable Banking
application ID + RSA private key through the authenticated setup wizard. The RSA key is encrypted at
rest with `Security:DataEncryptionKey` and is never returned to the browser after setup.

A global Enable Banking key is legacy-only and is resolved only for already-existing bank connections with no profile id. It cannot be used to create a new user bank connection. To migrate a legacy application, the legitimate application owner explicitly enters the same Application ID and matching PEM in their own Enable Banking settings wizard. FullWorth verifies it through /application and stores the new user-scoped copy encrypted; it never auto-assigns a global key to another user. For old deployments either set
`ENABLE_BANKING_APPLICATION_ID` plus `ENABLE_BANKING_PRIVATE_KEY_BASE64`, or keep the previous PEM
mount by starting Compose with `docker-compose.enable-banking-legacy.yml`.

```bash
docker compose config
docker compose up -d --build
```

Set the optional bootstrap user values only for the first start. Sign in, register a passkey, then
remove the bootstrap email and password from `.env` and recreate the affected service.

## Backup and restore

Back up both PostgreSQL and purchase files. Artifacts belong under the ignored `backups/` directory.

```bash
ops/backup/backup-all.sh
ops/restore-test/verify-restore.sh
```

Configure an offsite `rclone` remote with a dedicated, narrow-scope account. Restore verification
uses an isolated database and must be run regularly. A live restore is destructive; first create a
fresh backup, stop application services, and use the restore scripts only with their explicit
`--force` option.

## Secret rotation

Never commit a secret. Rotate paired service credentials on both sides before restarting the paired
services: Backend/Banking ingest, Web/Banking API, and Web/Backend internal access. Verify health,
login and a bank sync after rotation.

Do not replace `Security:DataEncryptionKey` in place when encrypted data exists. Existing data must
be decrypted with the old key and re-encrypted with the new one. Keep an old backup passphrase until
all backups encrypted with it have expired or been re-encrypted.



## Enable Banking private/restricted production

For personal testing, each FullWorth user should create their own Enable Banking Production application,
activate it by linking only their own accounts in the Enable Banking Control Panel, and then add that
application to FullWorth. Do not share one restricted application between unrelated FullWorth users.
Restricted production remains subject to Enable Banking's current Terms and linked-account rules.

The normal deployment does not require any Enable Banking PEM file. Legacy PEM compatibility:

```bash
mkdir -p secrets
# put the existing legacy RSA key here:
# secrets/enable-banking-private-key.pem
docker compose -f docker-compose.yml -f docker-compose.enable-banking-legacy.yml up -d
```
