#!/usr/bin/env bash
# M4 - Restore verification: prove a backup is actually restorable, in full isolation.
# Restores the latest (or given) Postgres dump into an EPHEMERAL container and the latest
# purchases archive into a THROWAWAY volume, then checks migrations, core tables, a basic
# relationship and the file manifest. Never touches the live database or volume.
#
# Usage:  ops/restore-test/verify-restore.sh [postgres-dump] [purchases-archive]
# Exit:   0 = PASS, non-zero = FAIL (with a logged reason).
set -euo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")/../backup" && pwd)/_common.sh"

require_cmd docker

latest() { ls -1t "$1" 2>/dev/null | head -n1 || true; }

DUMP="${1:-}"
if [[ -z "$DUMP" ]]; then
  f="$(latest "$BACKUP_ROOT/postgres/fullworth-*.dump" 2>/dev/null || true)"
  # ls glob with a path needs expansion; do it explicitly:
  f="$(ls -1t "$BACKUP_ROOT"/postgres/fullworth-*.dump 2>/dev/null | head -n1 || true)"
  DUMP="$f"
fi
[[ -n "$DUMP" && -f "$DUMP" ]] || die "no postgres dump found (looked in $BACKUP_ROOT/postgres). Run backup first or pass a path."

ARCHIVE="${2:-}"
if [[ -z "$ARCHIVE" ]]; then
  ARCHIVE="$(ls -1t "$BACKUP_ROOT"/purchases/purchases-*.tar.gz 2>/dev/null | head -n1 || true)"
fi

suffix="$(date -u +%Y%m%d%H%M%S)-$$"
PG_NAME="fullworth-restore-test-$suffix"
TEST_VOL="fullworth-restore-test-purchases-$suffix"
TEST_PW="restore-test-pw-$suffix"
FAILED=0

cleanup() {
  # -v also removes the container's anonymous data volume (postgres declares a VOLUME), so repeated
  # drills don't leak a full-DB-sized volume each run.
  docker rm -f -v "$PG_NAME" >/dev/null 2>&1 || true
  docker volume rm -f "$TEST_VOL" >/dev/null 2>&1 || true
}
trap cleanup EXIT

# Force TCP (PGHOST=127.0.0.1). The postgres entrypoint runs a temporary socket-only server during
# initdb; connecting over TCP guarantees we only ever talk to the real server, so readiness/restore
# can't race against that bootstrap phase.
pgx() { docker exec -e PGPASSWORD="$TEST_PW" -e PGHOST=127.0.0.1 "$PG_NAME" "$@"; }
psqlq() { pgx psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -tAc "$1"; }

log "starting ephemeral postgres ($PG_IMAGE) for restore test"
docker run -d --name "$PG_NAME" \
  -e POSTGRES_USER="$POSTGRES_USER" -e POSTGRES_DB="$POSTGRES_DB" -e POSTGRES_PASSWORD="$TEST_PW" \
  "$PG_IMAGE" >/dev/null

log "waiting for ephemeral postgres to accept connections"
for i in $(seq 1 30); do
  if pgx pg_isready -U "$POSTGRES_USER" -d "$POSTGRES_DB" >/dev/null 2>&1; then break; fi
  sleep 1
  [[ "$i" -eq 30 ]] && die "ephemeral postgres never became ready"
done

log "restoring dump $(basename "$DUMP") into ephemeral database"
docker exec -i -e PGPASSWORD="$TEST_PW" -e PGHOST=127.0.0.1 "$PG_NAME" \
  pg_restore -U "$POSTGRES_USER" -d "$POSTGRES_DB" --no-owner --no-privileges --single-transaction < "$DUMP" \
  || die "pg_restore into ephemeral database failed"

check() { # name, condition-command...
  local name="$1"; shift
  if "$@" >/dev/null 2>&1; then log "PASS  $name"; else log "FAIL  $name"; FAILED=1; fi
}

# 1) migrations applied
migrations="$(psqlq 'SELECT COUNT(*) FROM "__EFMigrationsHistory"' 2>/dev/null || echo 0)"
if [[ "${migrations:-0}" -ge 1 ]]; then log "PASS  migrations present ($migrations applied)"; else log "FAIL  migrations table empty/missing"; FAILED=1; fi

# 2) core tables exist and are queryable (structure restored). These are stable tables present
#    since the earliest schema, so the check proves a sound restore without assuming a specific
#    (possibly newer) migration level than the backup was taken at.
for t in FullWorthSpaces FullWorthSpaceMembers Accounts Transactions Categories Budgets NetWorthSnapshots; do
  if psqlq "SELECT COUNT(*) FROM \"$t\"" >/dev/null 2>&1; then
    log "PASS  table \"$t\" queryable ($(psqlq "SELECT COUNT(*) FROM \"$t\"") rows)"
  else
    log "FAIL  table \"$t\" missing"; FAILED=1
  fi
done
# Informational: total public tables restored (schema breadth), not a pass/fail gate.
total_tables="$(psqlq "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema='public'" 2>/dev/null || echo '?')"
log "INFO  $total_tables public tables restored"

# 3) basic relationship integrity: no space members pointing at a non-existent space
orphans="$(psqlq 'SELECT COUNT(*) FROM "FullWorthSpaceMembers" m WHERE NOT EXISTS (SELECT 1 FROM "FullWorthSpaces" s WHERE s."Id" = m."FullWorthSpaceId")' 2>/dev/null || echo 0)"
if [[ "${orphans:-0}" -eq 0 ]]; then log "PASS  no orphaned space members"; else log "FAIL  $orphans orphaned space members"; FAILED=1; fi

# 4) purchases archive restores + manifest verifies (if an archive exists)
if [[ -n "$ARCHIVE" && -f "$ARCHIVE" ]]; then
  log "verifying purchases archive $(basename "$ARCHIVE") into throwaway volume"
  if "$(dirname "${BASH_SOURCE[0]}")/../backup/purchases/restore.sh" --volume "$TEST_VOL" "$ARCHIVE" >/dev/null 2>&1; then
    log "PASS  purchases archive restored + manifest verified"
  else
    log "FAIL  purchases archive restore/verify failed"; FAILED=1
  fi
else
  log "SKIP  no purchases archive found (nothing to verify)"
fi

if [[ "$FAILED" -eq 0 ]]; then
  log "RESTORE VERIFICATION: PASS"
else
  log "RESTORE VERIFICATION: FAIL"
fi
exit "$FAILED"
