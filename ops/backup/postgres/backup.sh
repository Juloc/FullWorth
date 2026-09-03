#!/usr/bin/env bash
# M1 - PostgreSQL backup: consistent, timestamped, custom-format dump of the FullWorth
# database via the running compose stack. Applies retention and emits a checksum.
# The DB password is taken from the environment / .env at runtime, never embedded.
#
# Usage:  ops/backup/postgres/backup.sh
# Env:    FULLWORTH_BACKUP_DIR, FULLWORTH_BACKUP_RETENTION, POSTGRES_* (see _common.sh)
# Exit:   0 on success; non-zero (with a logged reason) on any failure.
set -euo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/_common.sh"

require_cmd docker

sha256_tool() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum "$@";
  elif command -v shasum   >/dev/null 2>&1; then shasum -a 256 "$@";
  else die "no sha256 tool (sha256sum/shasum) available"; fi
}

dest_dir="$BACKUP_ROOT/postgres"
mkdir -p "$dest_dir"

ts="$(timestamp)"
dump_file="$dest_dir/fullworth-$ts.dump"
tmp_file="$dump_file.partial"

# Verify the database is actually reachable before we start writing a file.
if ! "${COMPOSE[@]}" exec -T -e PGPASSWORD "$PG_SERVICE" \
      pg_isready -U "$POSTGRES_USER" -d "$POSTGRES_DB" >/dev/null 2>&1; then
  die "postgres service '$PG_SERVICE' is not ready (is the stack up?)"
fi

log "dumping database '$POSTGRES_DB' as '$POSTGRES_USER' -> $(basename "$dump_file")"
# -Fc: custom (compressed, selectively restorable) format. A single transaction snapshot
# gives a consistent dump. Stream stdout straight to the host file; fail hard if the dump errors.
if ! "${COMPOSE[@]}" exec -T -e PGPASSWORD "$PG_SERVICE" \
      pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" -Fc --no-owner --no-privileges > "$tmp_file"; then
  rm -f "$tmp_file"
  die "pg_dump failed"
fi

if [[ ! -s "$tmp_file" ]]; then
  rm -f "$tmp_file"
  die "pg_dump produced an empty file"
fi

mv "$tmp_file" "$dump_file"
( cd "$dest_dir" && sha256_tool "$(basename "$dump_file")" > "$(basename "$dump_file").sha256" )
log "wrote $(basename "$dump_file") ($(du -h "$dump_file" | cut -f1)) + .sha256"

prune_retention "$dest_dir" "fullworth-*.dump"
# Keep checksum files in step with their dumps.
for sum in "$dest_dir"/fullworth-*.dump.sha256; do
  [[ -e "$sum" ]] || continue
  [[ -e "${sum%.sha256}" ]] || { log "retention: removing orphan $(basename "$sum")"; rm -f "$sum"; }
done

log "postgres backup complete"
