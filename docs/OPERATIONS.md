# Operations

## Deploy

FullWorth is designed for a reverse proxy that terminates HTTPS and forwards only to FullWorth.Web.
Copy `.env.example` to `.env`, replace every placeholder with a unique value and set the public
hostname in `FULLWORTH_ALLOWED_HOSTS`, `FULLWORTH_PASSKEY_RP_ID` and `FULLWORTH_PASSKEY_ORIGIN`.

Required secrets include the database password, the three service credentials and the base64-encoded
32-byte data-encryption key. Create `secrets/enable-banking-private-key.pem`; replace the empty file
with the provider RSA key before enabling banking.

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

