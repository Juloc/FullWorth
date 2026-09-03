#!/usr/bin/env bash
# M2 - Purchase-file backup: archive the fullworth-purchases-data volume (receipt files)
# with a per-file sha256 integrity manifest. Files stay out of any web root - the archive
# is written under the backup directory only.
#
# Usage:  ops/backup/purchases/backup.sh
# Exit:   0 on success; non-zero (logged) otherwise.
set -euo pipefail
source "$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/_common.sh"

require_cmd docker
sha256_tool() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum "$@";
  elif command -v shasum   >/dev/null 2>&1; then shasum -a 256 "$@";
  else die "no sha256 tool (sha256sum/shasum) available"; fi
}

VOL="$(resolve_purchases_volume)"
docker volume inspect "$VOL" >/dev/null 2>&1 || die "purchases volume not found: $VOL"

dest_dir="$BACKUP_ROOT/purchases"
mkdir -p "$dest_dir"
ts="$(timestamp)"
archive="$dest_dir/purchases-$ts.tar.gz"
manifest="$dest_dir/purchases-$ts.manifest.sha256"
tmp_archive="$archive.partial"

file_count="$(docker run --rm -v "$VOL":/data:ro alpine sh -c 'cd /data && find . -type f | wc -l' | tr -d '[:space:]')"
log "archiving purchases volume '$VOL' ($file_count files)"

# Stream the gzip'd tar to the host file (no host bind mount -> portable across OSes).
if ! docker run --rm -v "$VOL":/data:ro alpine sh -c 'cd /data && tar czf - .' > "$tmp_archive"; then
  rm -f "$tmp_archive"; die "tar of purchases volume failed"
fi
mv "$tmp_archive" "$archive"

# Per-file checksums (paths relative to the volume root) so a restore can be verified file-by-file.
if ! docker run --rm -v "$VOL":/data:ro alpine sh -c 'cd /data && find . -type f -exec sha256sum {} +' > "$manifest.partial"; then
  rm -f "$manifest.partial"; die "manifest generation failed"
fi
mv "$manifest.partial" "$manifest"
if [[ "$file_count" -gt 0 && ! -s "$manifest" ]]; then
  die "manifest is empty despite $file_count files in the volume"
fi
( cd "$dest_dir" && sha256_tool "$(basename "$archive")" > "$(basename "$archive").sha256" )

log "wrote $(basename "$archive") ($(du -h "$archive" | cut -f1)), manifest with $(wc -l < "$manifest" | tr -d '[:space:]') entries"

prune_retention "$dest_dir" "purchases-*.tar.gz"
# Drop manifests/checksums whose archive was pruned.
for extra in "$dest_dir"/purchases-*.manifest.sha256 "$dest_dir"/purchases-*.tar.gz.sha256; do
  [[ -e "$extra" ]] || continue
  base="${extra%.manifest.sha256}"; base="${base%.tar.gz.sha256}"
  [[ -e "$base.tar.gz" ]] || { log "retention: removing orphan $(basename "$extra")"; rm -f "$extra"; }
done

log "purchases backup complete"
