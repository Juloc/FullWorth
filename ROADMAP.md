# Roadmap

FullWorth is a self-hosted personal finance application for individuals and shared FullWorth Spaces.

## Product scope

- Accounts, balances and transactions
- Categories, rules, merchants, transfers and refunds
- Budgets, contracts, loans, assets, liabilities and net worth
- Purchases, receipts and data imports
- Compensation (Gehalt & Benefits), payslips and salary comparison
- Analytics, spending reviews and multi-currency (FX) conversion
- Tax assistant with year reviews and tax export (Germany)
- AI coach and intelligence suggestions, with optional cloud benchmarks
- Notifications and web push
- Data export and portable backups
- Password and passkey sign-in, sessions, recovery codes and sharing
- Optional Enable Banking and FinTS (ING) bank connections with conservative background synchronisation
- Responsive, installable web application in German and English

## Current focus

The project is in beta. The priority is dependable self-hosting and safe handling of real financial
data:

1. Validate bank connections with supported institutions and providers.
2. Exercise backup, restore, migration and upgrade procedures on real deployments.
3. Improve import, reconciliation, receipt and notification workflows.
4. Finish accessibility, responsive UI and performance reviews.
5. Keep security, dependency and container checks part of every release.

## Release criteria

A production release requires working authentication and recovery, scoped authorization for every
resource, secure browser-to-service boundaries, tested backups and restores, and no known critical
security issues. See [Security](docs/SECURITY_ARCHITECTURE.md), [Operations](docs/OPERATIONS.md)
and [Release](docs/RELEASE.md) for the corresponding checklists.
