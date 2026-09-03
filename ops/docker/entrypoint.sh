#!/bin/sh
# Runtime entrypoint for the services that own a writable data volume (backend, web).
# Docker mounts named volumes as root; make the app's data dirs writable, then drop privileges
# and run the application as the non-root "app" user. Works for both fresh and pre-existing
# (root-owned) volumes, so hardening an already-deployed stack needs no manual chown.
set -e

for d in /data/purchases /data/dataprotection; do
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
