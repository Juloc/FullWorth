#!/bin/sh
set -eu

mkdir -p "${CODEX_HOME:-/data/codex}" "${CODEX_WORKDIR:-/tmp/codex-work}"
chown -R node:node "${CODEX_HOME:-/data/codex}" "${CODEX_WORKDIR:-/tmp/codex-work}"

exec gosu node "$@"
