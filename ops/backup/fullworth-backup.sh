#!/usr/bin/env bash
# Encrypted backup of the FullWorth PostgreSQL database + receipt files
# (SECURITY_ARCHITECTURE "Backup security", work item P0.5).
#
# Backups are ENCRYPTED before they touch disk/remote, with a DEDICATED credential (not the app's DB
# or encryption keys), plus a retention sweep. Restore-test the output regularly (see bottom).
#
# Required environment:
#   PGHOST, PGPORT, PGDATABASE, PGUSER, PGPASSWORD  -- a read-capable backup DB role (its own account,
#                                                      separate from the app role)
#   RECEIPTS_DIR            -- purchase/receipt storage root (default /data/purchases)
#   BACKUP_DIR             -- output directory (default /backups)
#   BACKUP_PASSPHRASE_FILE -- file holding the backup encryption passphrase (a dedicated secret)
#   BACKUP_RETENTION_DAYS  -- delete encrypted backups older than this (default 30)
#
# Encryption: age (preferred) if available, else gpg, else openssl AES-256 (pbkdf2). All read the same
# passphrase file; none of them is the application's DataEncryptionKey.

set -euo pipefail

: "${PGDATABASE:=fullworth}"
: "${RECEIPTS_DIR:=/data/purchases}"
: "${BACKUP_DIR:=/backups}"
: "${BACKUP_RETENTION_DAYS:=30}"
: "${BACKUP_PASSPHRASE_FILE:?BACKUP_PASSPHRASE_FILE must point at the backup passphrase secret}"

[ -r "$BACKUP_PASSPHRASE_FILE" ] || { echo "backup passphrase file is not readable" >&2; exit 1; }
mkdir -p "$BACKUP_DIR"
stamp="$(date -u +%Y%m%dT%H%M%SZ)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

encrypt() {
  # $1 = plaintext input file, $2 = encrypted output file
  local in="$1" out="$2" pass; pass="$(cat "$BACKUP_PASSPHRASE_FILE")"
  if command -v age >/dev/null 2>&1; then
    printf '%s' "$pass" | age --passphrase --output "$out" "$in" >/dev/null 2>&1 \
      || age -p -o "$out" "$in"
  elif command -v gpg >/dev/null 2>&1; then
    gpg --batch --yes --passphrase "$pass" --symmetric --cipher-algo AES256 -o "$out" "$in"
  else
    openssl enc -aes-256-cbc -pbkdf2 -salt -in "$in" -out "$out" -pass "pass:$pass"
  fi
}

# 1) Database dump (custom format, compressed) -> encrypted.
db_plain="$work/fullworth-db-$stamp.dump"
pg_dump --format=custom --file="$db_plain" "$PGDATABASE"
encrypt "$db_plain" "$BACKUP_DIR/fullworth-db-$stamp.dump.enc"

# 2) Receipt files (if present) -> tar.gz -> encrypted.
if [ -d "$RECEIPTS_DIR" ]; then
  receipts_plain="$work/fullworth-receipts-$stamp.tar.gz"
  tar -czf "$receipts_plain" -C "$RECEIPTS_DIR" .
  encrypt "$receipts_plain" "$BACKUP_DIR/fullworth-receipts-$stamp.tar.gz.enc"
fi

# 3) Retention: remove encrypted backups older than the retention window.
find "$BACKUP_DIR" -type f -name '*.enc' -mtime "+$BACKUP_RETENTION_DAYS" -delete

echo "Backup complete: $BACKUP_DIR (stamp $stamp)"

# Restore test (run periodically against a throwaway database/dir):
#   age -d -o restore.dump   fullworth-db-<stamp>.dump.enc     # or gpg -d / openssl enc -d
#   pg_restore --clean --if-exists --dbname=fullworth_restore_test restore.dump
#   age -d -o restore.tgz    fullworth-receipts-<stamp>.tar.gz.enc && tar -tzf restore.tgz | head
