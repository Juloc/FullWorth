#!/usr/bin/env bash
# M2 - Purchase-file restore: extract a purchases archive into a docker volume and verify
# each file against the sha256 manifest. Restoring into the live volume requires --force;
# a --volume <name> target (e.g. a throwaway test volume) is allowed freely.
#
# Usage:  ops/backup/purchases/restore.sh [--force] [--volume NAME] <archive.tar.gz>
# Exit:   0 on success; non-zero (logged) otherwise.
set -euo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/_common.sh"

require_cmd docker

FORCE=0
TARGET_VOL=""
ARCHIVE=""
while (( $# )); do
  case "$1" in
    --force)  FORCE=1; shift ;;
    --volume) TARGET_VOL="${2:?--volume needs a value}"; shift 2 ;;
    -*)       die "unknown option: $1" ;;
    *)        ARCHIVE="$1"; shift ;;
  esac
done
[[ -n "$ARCHIVE" ]] || die "usage: restore.sh [--force] [--volume NAME] <archive.tar.gz>"
[[ -f "$ARCHIVE" ]] || die "archive not found: $ARCHIVE"

LIVE_VOL="$(resolve_purchases_volume)"
TARGET_VOL="${TARGET_VOL:-$LIVE_VOL}"

if [[ "$TARGET_VOL" == "$LIVE_VOL" && "$FORCE" -ne 1 ]]; then
  die "refusing to restore into the live purchases volume '$LIVE_VOL' without --force (or use --volume <other>)"
fi

# Verify the archive's own checksum first.
if [[ -f "$ARCHIVE.sha256" ]]; then
  log "verifying archive checksum"
  ( cd "$(dirname "$ARCHIVE")" && \
    { command -v sha256sum >/dev/null 2>&1 && sha256sum -c "$(basename "$ARCHIVE").sha256" \
      || shasum -a 256 -c "$(basename "$ARCHIVE").sha256"; } ) >/dev/null \
    || die "archive checksum verification failed"
fi

docker volume create "$TARGET_VOL" >/dev/null
# Faithful restore: empty the target first so files present in the volume but absent from the
# archive (e.g. receipts deleted after the backup) do not silently survive. This runs only after
# the --force live-volume gate above.
docker run --rm -v "$TARGET_VOL":/data alpine sh -c 'find /data -mindepth 1 -delete' \
  || die "failed to clear target volume before restore"
log "extracting $(basename "$ARCHIVE") into volume '$TARGET_VOL'"
docker run --rm -i -v "$TARGET_VOL":/data alpine sh -c 'cd /data && tar xzf -' < "$ARCHIVE" \
  || die "extraction failed"

# Per-file verification against the manifest, if present.
manifest="${ARCHIVE%.tar.gz}.manifest.sha256"
if [[ -f "$manifest" && -s "$manifest" ]]; then
  log "verifying restored files against manifest"
  docker run --rm -i -v "$TARGET_VOL":/data:ro alpine sh -c 'cd /data && sha256sum -c -' < "$manifest" >/dev/null \
    || die "restored files failed manifest verification"
  log "manifest verification passed ($(wc -l < "$manifest" | tr -d '[:space:]') files)"
fi

log "purchases restore complete into '$TARGET_VOL'"
