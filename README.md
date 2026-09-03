# FullWorth

FullWorth is a self-hosted personal finance application. It helps manage accounts, transactions, budgets, contracts, purchases, and optional bank connections.

## Run

FullWorth is intended to run behind an HTTPS reverse proxy. You need Docker and Docker Compose.

```bash
cp .env.example .env
# Set the required values in .env.
mkdir -p secrets
# Compose requires this file. Replace it with the RSA key before enabling Enable Banking.
touch secrets/enable-banking-private-key.pem
docker compose up -d --build
```

Set strong, unique values for the database password, service keys, and data-encryption key. Configure `FULLWORTH_ALLOWED_HOSTS`, `FULLWORTH_PASSKEY_RP_ID`, and `FULLWORTH_PASSKEY_ORIGIN` for the public HTTPS hostname before exposing the application.

## Documentation

Deployment, backup, and security documentation is available in [docs](docs/). The project is currently in beta; use it with backups and care.

## License

This project is licensed under the [MIT License](LICENSE).
