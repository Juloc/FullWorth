#!/usr/bin/env bash
# Orchestrator: run the Postgres and purchase-file backups, then optionally push offsite.
# Intended to be the single entry point for a cron/systemd schedule.
#
# Usage:  ops/backup/backup-all.sh
# Offsite upload runs only when FULLWORTH_RCLONE_REMOTE is set.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$HERE/_common.sh"

log "=== FullWorth backup run starting ==="
"$HERE/postgres/backup.sh"
"$HERE/purchases/backup.sh"

if [[ -n "${FULLWORTH_RCLONE_REMOTE:-}" ]]; then
  "$HERE/offsite/upload-rclone.sh"
else
  log "offsite: FULLWORTH_RCLONE_REMOTE not set, skipping remote upload"
fi

log "=== FullWorth backup run complete ==="
