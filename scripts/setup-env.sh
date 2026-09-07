#!/usr/bin/env sh
set -eu

if [ "$#" -ne 2 ]; then
  echo "Usage: sh scripts/setup-env.sh fullworth.example.com admin@example.com" >&2
  exit 1
fi

domain="$1"
admin_email="$2"
case "$domain" in
  http://*|https://*|*/*|*" "*)
    echo "Use only the hostname, e.g. fullworth.example.com" >&2
    exit 1
    ;;
esac

if ! command -v openssl >/dev/null 2>&1; then
  echo "openssl is required." >&2
  exit 1
fi

if [ -e .env ]; then
  echo ".env already exists; refusing to overwrite it." >&2
  exit 1
fi

umask 077
cp .env.example .env

replace_value() {
  key="$1"
  value="$2"
  tmp="$(mktemp)"
  awk -v key="$key" -v value="$value" '
    index($0, key "=") == 1 { print key "=" value; next }
    { print }
  ' .env > "$tmp"
  mv "$tmp" .env
}

replace_value FULLWORTH_DOMAIN "$domain"
replace_value POSTGRES_PASSWORD "$(openssl rand -hex 24)"
replace_value FULLWORTH_BACKEND_INTERNAL_KEY "$(openssl rand -hex 32)"
replace_value FULLWORTH_INGEST_KEY "$(openssl rand -hex 32)"
replace_value FULLWORTH_BANKING_API_KEY "$(openssl rand -hex 32)"
replace_value FULLWORTH_DATA_ENCRYPTION_KEY "$(openssl rand -base64 32 | tr -d '\n')"

admin_password="$(openssl rand -hex 18)"
printf '\nFULLWORTH_BOOTSTRAP_EMAIL=%s\n' "$admin_email" >> .env
printf 'FULLWORTH_BOOTSTRAP_PASSWORD=%s\n' "$admin_password" >> .env

# Enable the isolated Codex bridge securely. Authentication with Codex/ChatGPT is still optional
# and is completed by each user from the FullWorth first-login setup or Settings.
printf '\nFULLWORTH_CODEX_BRIDGE_KEY=%s\n' "$(openssl rand -hex 32)" >> .env

echo "Created .env for $domain"
echo "Initial admin: $admin_email"
echo "Initial password: $admin_password"
echo "Remove FULLWORTH_BOOTSTRAP_EMAIL/PASSWORD from .env after the first successful sign-in."
echo "Next: docker compose pull && docker compose up -d"
