#!/usr/bin/env bash
# M1 - PostgreSQL restore: restore a custom-format dump produced by backup.sh into the
# running compose Postgres. Restoring OVER the live database is destructive and therefore
# requires an explicit --force; restoring into a different --db name is allowed freely.
#
# Usage:  ops/backup/postgres/restore.sh [--force] [--db NAME] <dump-file>
# Exit:   0 on success; non-zero (logged) otherwise.
set -euo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/_common.sh"

require_cmd docker

FORCE=0
TARGET_DB="$POSTGRES_DB"
DUMP_FILE=""
while (( $# )); do
  case "$1" in
    --force) FORCE=1; shift ;;
    --db)    TARGET_DB="${2:?--db needs a value}"; shift 2 ;;
    -*)      die "unknown option: $1" ;;
    *)       DUMP_FILE="$1"; shift ;;
  esac
done
[[ -n "$DUMP_FILE" ]] || die "usage: restore.sh [--force] [--db NAME] <dump-file>"
[[ -f "$DUMP_FILE" ]] || die "dump file not found: $DUMP_FILE"

# Verify integrity if a checksum sits next to the dump.
if [[ -f "$DUMP_FILE.sha256" ]]; then
  log "verifying checksum"
  ( cd "$(dirname "$DUMP_FILE")" && \
    { command -v sha256sum >/dev/null 2>&1 && sha256sum -c "$(basename "$DUMP_FILE").sha256" \
      || shasum -a 256 -c "$(basename "$DUMP_FILE").sha256"; } ) >/dev/null \
    || die "checksum verification failed for $DUMP_FILE"
fi

if [[ "$TARGET_DB" == "$POSTGRES_DB" && "$FORCE" -ne 1 ]]; then
  die "refusing to overwrite the live database '$POSTGRES_DB' without --force (or restore into --db <other>)"
fi

pg() { "${COMPOSE[@]}" exec -T -e PGPASSWORD "$PG_SERVICE" "$@"; }

# Create the target database if it does not exist (safe for a non-live --db target).
if ! pg psql -U "$POSTGRES_USER" -d postgres -tAc \
      "SELECT 1 FROM pg_database WHERE datname='$TARGET_DB'" | grep -q 1; then
  log "creating database '$TARGET_DB'"
  pg createdb -U "$POSTGRES_USER" "$TARGET_DB"
fi

log "restoring $(basename "$DUMP_FILE") into database '$TARGET_DB' (clean/if-exists)"
# --single-transaction so a failed restore rolls back rather than leaving a half state.
if ! pg pg_restore -U "$POSTGRES_USER" -d "$TARGET_DB" \
      --clean --if-exists --no-owner --no-privileges --single-transaction < "$DUMP_FILE"; then
  die "pg_restore failed"
fi

log "restore complete into '$TARGET_DB'"
