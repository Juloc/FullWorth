#!/bin/sh
# Runtime entrypoint for the services that own a writable data volume (backend, web).
# Docker mounts named volumes as root; make the app's data dirs writable, then drop privileges
# and run the application as the non-root "app" user. Works for both fresh and pre-existing
# (root-owned) volumes, so hardening an already-deployed stack needs no manual chown.
set -e

# Created, not just chowned. The image NAMES these paths in its own ENV (DataProtection__KeyPath,
# PurchaseStorage__RootPath, PensionStorage__RootPath), so it has to be able to run without a volume
# mounted over them - otherwise the app starts as the non-root user, tries to create /data itself and
# dies with "Access to the path '/data' is denied" before it reaches a single line of configuration.
# With a volume mounted this is a no-op on an existing directory and a chown on a fresh one.
#
# 0700, not just chowned. Docker creates a fresh named volume 0755, which was harmless while one user
# lived in this container and is not any more: /data/dataprotection holds the key ring that signs the
# sign-in cookies, and a second uid that can read it can forge a session. Measured in a running
# container, where codex could list that directory although it had no business knowing it exists.
for d in /data/purchases /data/pension /data/dataprotection; do
  mkdir -p "$d" 2>/dev/null || true
  if [ -d "$d" ]; then
    chown -R app:app "$d" 2>/dev/null || true
    chmod 0700 "$d" 2>/dev/null || true
  fi
done

# The secrets directory, for the same reason and one step further: the app CREATES the secrets it owns
# in here on a host that does not have them. Docker mounts a fresh named volume root-owned, and only
# the directory's owner may create files in it - so without this the app silently failed to write all
# five of its own secrets and died on "Services:BackendInternalKey must be configured", which points
# at configuration rather than at a permission.
#
# Only the directory: the files inside are chowned by whoever owns them (postgres writes the database
# password here and hands it to the app user itself), and taking ownership of a secret this process
# does not own would be the wrong kind of helpful.
SECRETS=/run/fullworth-secrets
mkdir -p "$SECRETS" 2>/dev/null || true
[ -d "$SECRETS" ] && chown app:app "$SECRETS" 2>/dev/null || true

# 0700, and this is the gate that matters now that a second user lives in this container. Codex cannot
# traverse this directory at all, so it cannot reach the data encryption key, the internal key or the
# database password no matter what mode those files happen to carry - and fullworth_db_password does
# carry a wide one, because the postgres container has to read it as its own user across the volume.
#
# Tightening the files this process owns is belt and braces for installations created before the modes
# were set at write time. fullworth_db_password is deliberately not in the list: it belongs to postgres.
chmod 0700 "$SECRETS" 2>/dev/null || true
for f in data_encryption_key backend_internal_key ingest_key banking_api_key; do
  [ -f "$SECRETS/$f" ] && chmod 0600 "$SECRETS/$f" 2>/dev/null || true
done

# ---------------------------------------------------------------------------------------------------
# Codex.
#
# It used to be a second container. It is a second USER in this one now, and the whole separation is
# file ownership: codex cannot read what app owns, app cannot read the ChatGPT session codex owns.
# This script is the only process here that is root, so this is the only place it can be established.

CODEX_ARM_DIR=${CODEX_ARM_DIR:-/tmp/fullworth-codex}
CODEX_HOME=${CODEX_HOME:-/data/codex}
CODEX_WORKDIR=${CODEX_WORKDIR:-/tmp/codex-work}
CODEX_KEY=${CODEX_BRIDGE_KEY_PATH:-$CODEX_ARM_DIR/bridge_key}

if id -u codex >/dev/null 2>&1; then
  # The Codex login, and the scratch directory it unpacks uploads into. 0700 codex:codex - the
  # application must not be able to read a user's ChatGPT session either. On an upgrade from the
  # sidecar this volume is owned by uid 1000, which is "ubuntu" in this base image.
  for d in "$CODEX_HOME" "$CODEX_WORKDIR"; do
    mkdir -p "$d" 2>/dev/null || true
    chown -R codex:codex "$d" 2>/dev/null || true
    chmod 0700 "$d" 2>/dev/null || true
  done

  # The handover directory: app writes the arm file, codex reads it. 0750 app:codex means codex can
  # see what is in here and nothing else, and cannot plant anything.
  mkdir -p "$CODEX_ARM_DIR" 2>/dev/null || true
  chown app:codex "$CODEX_ARM_DIR" 2>/dev/null || true
  chmod 0750 "$CODEX_ARM_DIR" 2>/dev/null || true

  # The bridge key is created HERE rather than by the application, because it is the one secret with
  # two readers under two user ids and this is the only moment anything is root. Create-only: a key
  # that exists is never replaced, or a restart would log every user out of Codex.
  if [ ! -s "$SECRETS/codex_bridge_key" ]; then
    ( umask 077; head -c 48 /dev/urandom | base64 | tr -d '\012' > "$SECRETS/codex_bridge_key" ) 2>/dev/null || true
  fi
  # 0640 app:app: the application reads it as the owner. The group bit is there for a stack that
  # deliberately bind-mounts this one file into another container - the Cloud's AI review runs as
  # 1654:1654, which is app's group.
  chown app:app "$SECRETS/codex_bridge_key" 2>/dev/null || true
  chmod 0640 "$SECRETS/codex_bridge_key" 2>/dev/null || true

  # Codex gets a copy on tmpfs instead of a path into the secrets directory. That is what lets the
  # directory stay 0700: the bridge needs exactly this one value and never has a reason to be able to
  # open the store that holds the others.
  if [ -s "$SECRETS/codex_bridge_key" ]; then
    cp "$SECRETS/codex_bridge_key" "$CODEX_KEY" 2>/dev/null || true
    chown codex:codex "$CODEX_KEY" 2>/dev/null || true
    chmod 0400 "$CODEX_KEY" 2>/dev/null || true
  fi

  # Already signed in? Then arm it now, so "always ready" is true from the first request rather than
  # from the first request plus a Node start. An installation that never signed in stays at a sleeping
  # shell until somebody opens the Codex screens.
  if [ -n "$(ls -A "$CODEX_HOME" 2>/dev/null)" ]; then
    : > "$CODEX_ARM_DIR/arm" 2>/dev/null || true
    chown app:codex "$CODEX_ARM_DIR/arm" 2>/dev/null || true
  fi

  # Backgrounded before the exec below, so it survives as a child of PID 1. The app service needs
  # init: true for its exits to be reaped - PID 1 is the .NET process after the exec and does not
  # adopt-and-reap.
  gosu codex /usr/local/bin/fullworth-codex-launcher &
fi

# Stage the Enable Banking private key so the non-root app can read it regardless of the host
# secret's owner/mode (a docker file-secret is mounted with the host file's permissions, and a
# properly-secured 0400 root key would otherwise be unreadable by the app user). Copy to a tmpfs
# path owned 0400 by app and point the app at it.
# Docker mounts a file secret at /run/secrets/<secret-name> (no file extension).
SRC=/run/secrets/enable-banking-private-key
if [ -f "$SRC" ]; then
  DST=/tmp/enable-banking-private-key.pem
  cp "$SRC" "$DST"
  chown app:app "$DST"
  chmod 400 "$DST"
  export EnableBanking__PrivateKeyPath="$DST"
fi

exec gosu app "$@"
