# FullWorth

FullWorth is a self-hosted personal finance application. It helps manage accounts, transactions, budgets, contracts, purchases, and optional bank connections.

## Run

FullWorth is intended to run behind an HTTPS reverse proxy. You need Docker and Docker Compose.

```bash
cp .env.example .env
# Set the required values in .env, including ENABLE_BANKING_REDIRECT_URL.
docker compose up -d --build
```

Set strong, unique values for the database password, service keys, and data-encryption key. Configure `FULLWORTH_ALLOWED_HOSTS`, `FULLWORTH_PASSKEY_RP_ID`, `FULLWORTH_PASSKEY_ORIGIN`, and `ENABLE_BANKING_REDIRECT_URL` for the public HTTPS hostname before exposing the application.

Enable Banking is BYO by default: each FullWorth user configures their own Enable Banking application and RSA key from the FullWorth settings wizard. A global PEM file is no longer required to boot the stack. Existing deployments that still use one global Enable Banking application can use `docker-compose.enable-banking-legacy.yml` during migration.

## Documentation

Deployment, backup, and security documentation is available in [docs](docs/). The project is currently in beta; use it with backups and care.

## License

This project is licensed under the [MIT License](LICENSE).
