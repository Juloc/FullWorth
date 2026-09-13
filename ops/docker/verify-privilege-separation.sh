#!/bin/sh
# Checks that the boundary between the application and Codex is really there.
#
# It is made of file ownership and nothing else, so the only way to check it is to run the entrypoint
# and then try. Both holes found so far were invisible to every other kind of test: secrets written
# world-readable, and a data-protection key ring a second uid could list — with which it could forge
# a sign-in cookie.
#
# Runs INSIDE a container whose entrypoint has already executed. CI does that; so can a person:
#   docker run --rm --entrypoint /usr/local/bin/entrypoint.sh <image> \
#     /usr/local/bin/verify-privilege-separation
set -eu

failures=0

check() {
  description=$1
  shift
  if "$@" >/dev/null 2>&1; then
    echo "  ok      $description"
  else
    echo "  FAILED  $description" >&2
    failures=$((failures + 1))
  fi
}

# The same call, inverted: these must NOT work.
refuse() {
  description=$1
  shift
  if "$@" >/dev/null 2>&1; then
    echo "  FAILED  $description" >&2
    failures=$((failures + 1))
  else
    echo "  ok      $description"
  fi
}

echo "Codex may reach exactly one thing:"
check  "codex reads its own bridge key"          gosu codex sh -c 'cat /tmp/fullworth-codex/bridge_key'
refuse "codex cannot read the internal key"      gosu codex sh -c 'cat /run/fullworth-secrets/backend_internal_key'
refuse "codex cannot read the data key"          gosu codex sh -c 'cat /run/fullworth-secrets/data_encryption_key'
refuse "codex cannot list the secret store"      gosu codex sh -c 'ls /run/fullworth-secrets'
refuse "codex cannot list the key ring"          gosu codex sh -c 'ls /data/dataprotection'
refuse "codex cannot list receipts"              gosu codex sh -c 'ls /data/purchases'
refuse "codex cannot list pension documents"     gosu codex sh -c 'ls /data/pension'

echo "And the application cannot reach back:"
refuse "the app cannot read the Codex login"     gosu app sh -c 'ls /data/codex'
refuse "the app cannot read the key copy"        gosu app sh -c 'cat /tmp/fullworth-codex/bridge_key'
check  "the app reads its own secrets"           gosu app sh -c 'cat /run/fullworth-secrets/backend_internal_key'

if [ "$failures" -gt 0 ]; then
  echo "$failures check(s) failed." >&2
  exit 1
fi

echo "Privilege separation holds."
