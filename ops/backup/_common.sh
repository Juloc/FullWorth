#!/usr/bin/env bash
# Shared configuration and helpers for the FullWorth backup/restore scripts.
# Sourced by the postgres/, purchases/ and restore-test scripts. No secrets live here;
# the database password is read at runtime from the environment or the compose .env file.
set -euo pipefail

# Backups contain the entire database (including bank tokens) and receipt files, so every file
# and directory this tooling creates must be owner-only.
umask 077

# --- repo root -------------------------------------------------------------
if REPO_ROOT="$(git -C "$(dirname "${BASH_SOURCE[0]}")" rev-parse --show-toplevel 2>/dev/null)"; then
  :
else
  # Fallback when not run from a git checkout: ops/backup/_common.sh -> repo root is two levels up.
  REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
fi
export REPO_ROOT

# --- load the compose env file (POSTGRES_*, etc.) --------------------------
# A docker-compose .env is data, not a shell script: values may contain spaces, quotes, '$', '#'
# or shell metacharacters that would break (or execute) under `source`. Parse KEY=VALUE lines
# literally instead. Existing environment values win (compose precedence). Secrets loaded here are
# only used to reach the database and are never written to any committed file.
ENV_FILE="${FULLWORTH_ENV_FILE:-$REPO_ROOT/.env}"
if [[ -f "$ENV_FILE" ]]; then
  while IFS= read -r _line || [[ -n "$_line" ]]; do
    _line="${_line%$'\r'}"                                   # tolerate CRLF-edited files
    [[ "$_line" =~ ^[[:space:]]*(#|$) ]] && continue         # skip comments/blank
    [[ "$_line" == *=* ]] || continue
    _key="${_line%%=*}"; _val="${_line#*=}"
    _key="${_key#"${_key%%[![:space:]]*}"}"; _key="${_key%"${_key##*[![:space:]]}"}"  # trim key
    [[ "$_key" =~ ^[A-Za-z_][A-Za-z0-9_]*$ ]] || continue
    _val="${_val%%[[:space:]]#*}"                            # drop trailing " # comment"
    if [[ "$_val" == \"*\" || "$_val" == \'*\' ]]; then _val="${_val:1:${#_val}-2}"; fi  # unquote
    [[ -n "${!_key:-}" ]] && continue                        # shell env overrides .env
    export "$_key=$_val"
  done < "$ENV_FILE"
  unset _line _key _val
fi

# --- effective configuration (mirrors docker-compose.yml defaults) ---------
POSTGRES_DB="${POSTGRES_DB:-fullworth}"
POSTGRES_USER="${POSTGRES_USER:-fullworth}"
# Export PGPASSWORD so pg_* clients pick it up from the environment. Passing it via a bare
# `-e PGPASSWORD` (name only) keeps the secret off the docker CLI argv, which is world-readable
# via /proc/<pid>/cmdline; the process environment is not.
export PGPASSWORD="${POSTGRES_PASSWORD:-}"
PG_SERVICE="${FULLWORTH_PG_SERVICE:-fullworth-postgres}"
PG_IMAGE="${FULLWORTH_PG_IMAGE:-postgres:18-alpine}"

# docker compose invocation, pinned to this repo so scripts work from any cwd.
COMPOSE=(docker compose --project-directory "$REPO_ROOT")
if [[ -n "${FULLWORTH_COMPOSE_FILE:-}" ]]; then
  COMPOSE+=(-f "$FULLWORTH_COMPOSE_FILE")
fi

# Compose project name (used to derive named-volume names). Defaults to the compose
# convention: the project directory's basename, lowercased.
PROJECT_NAME="${COMPOSE_PROJECT_NAME:-$(basename "$REPO_ROOT" | tr '[:upper:]' '[:lower:]')}"

# Where backup artifacts are written. Kept out of git (see ops/backup/.gitignore).
BACKUP_ROOT="${FULLWORTH_BACKUP_DIR:-$REPO_ROOT/backups}"
RETENTION="${FULLWORTH_BACKUP_RETENTION:-14}"

# --- helpers ---------------------------------------------------------------
log()  { printf '%s [backup] %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$*" >&2; }
die()  { printf '%s [backup][ERROR] %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$*" >&2; exit 1; }

timestamp() { date -u +%Y%m%d-%H%M%S; }

require_cmd() { command -v "$1" >/dev/null 2>&1 || die "required command not found: $1"; }

# Resolve the docker named volume that backs fullworth-purchases-data. Prefers the
# compose label (robust to project renames), falling back to the naming convention.
resolve_purchases_volume() {
  if [[ -n "${FULLWORTH_PURCHASES_VOLUME:-}" ]]; then
    printf '%s' "$FULLWORTH_PURCHASES_VOLUME"; return 0
  fi
  local by_label
  by_label="$(docker volume ls -q \
    -f "label=com.docker.compose.project=$PROJECT_NAME" \
    -f "label=com.docker.compose.volume=fullworth-purchases-data" 2>/dev/null | head -n1 || true)"
  if [[ -n "$by_label" ]]; then printf '%s' "$by_label"; return 0; fi
  printf '%s_fullworth-purchases-data' "$PROJECT_NAME"
}

# Prune all but the newest $RETENTION files matching a glob in a directory.
prune_retention() {
  local dir="$1" glob="$2"
  local -a files
  mapfile -t files < <(ls -1t "$dir"/$glob 2>/dev/null || true)
  if (( ${#files[@]} > RETENTION )); then
    local i
    for (( i=RETENTION; i<${#files[@]}; i++ )); do
      log "retention: removing old backup $(basename "${files[$i]}")"
      rm -f -- "${files[$i]}"
    done
  fi
}
