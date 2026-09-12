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
for d in /data/purchases /data/pension /data/dataprotection; do
  mkdir -p "$d" 2>/dev/null || true
  if [ -d "$d" ]; then
    chown -R app:app "$d" 2>/dev/null || true
  fi
done

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
