#!/usr/bin/env bash
# M3 - Offsite backup destination via rclone (works with Google Drive and many others).
# Credentials live in rclone's own config (outside this repo); this script only references
# a preconfigured remote by name. See docs/BACKUP.md for least-privilege setup.
#
# Usage:  FULLWORTH_RCLONE_REMOTE=gdrive:fullworth-backups ops/backup/offsite/upload-rclone.sh
# Env:    FULLWORTH_RCLONE_REMOTE (required, "remote:path"), FULLWORTH_RCLONE_FLAGS (optional)
set -euo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/_common.sh"

require_cmd rclone
REMOTE="${FULLWORTH_RCLONE_REMOTE:?set FULLWORTH_RCLONE_REMOTE=remote:path (e.g. gdrive:fullworth-backups)}"
# shellcheck disable=SC2206
FLAGS=(${FULLWORTH_RCLONE_FLAGS:---transfers=2 --checksum})

upload() {
  local src="$1" dst="$2" include="$3"
  [[ -d "$src" ]] || { log "offsite: no local dir $src, skipping"; return 0; }
  log "offsite: uploading $include from $src -> $REMOTE/$dst"
  rclone copy "$src" "$REMOTE/$dst" --include "$include" "${FLAGS[@]}"
}

# Push dumps, purchase archives and their checksums/manifests.
upload "$BACKUP_ROOT/postgres"   "postgres"   "fullworth-*.dump*"
upload "$BACKUP_ROOT/purchases"  "purchases"  "purchases-*"

log "offsite upload complete -> $REMOTE"
