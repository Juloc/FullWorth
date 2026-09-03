# Amazon order sync

FullWorth imports personal Amazon.de orders into the existing `Purchase` / `PurchaseItem` flow. Amazon does not expose ordinary buyer order history through a public customer API, so this connector uses a pinned Playwright/Chromium runtime against the user's own Amazon account.

## User flow

1. Open **Käufe** and choose **Amazon verbinden**.
2. Enter Amazon email and password. These values are used only for the active login request and are never persisted.
3. If Amazon asks for an OTP, enter it. If Amazon asks for device approval, approve the sign-in and continue.
4. FullWorth stores only Playwright's browser storage state, encrypted with `Security:DataEncryptionKey` through `FieldCipher`.
5. The first successful connection syncs the last 90 days. The dialog can also sync 1 year or **Alle**. For **Alle**, FullWorth first reads the years Amazon actually exposes in the order-history filter; a bounded 1995 fallback is used only if that filter cannot be read.
6. Connected accounts are refreshed automatically every 24 hours and can be synced manually from **Käufe**.

CAPTCHAs are not bypassed. When Amazon requests a CAPTCHA or the session expires, the connection changes to `requires_reauth` and the user reconnects.

## Imported data

For each order FullWorth keeps:

- Amazon order number
- purchase date and order total/currency
- order detail URL
- external order status
- item name, ASIN, quantity, unit price and line total when Amazon exposes them
- detected Amazon gift-card/account-balance payment amount
- detected returns and refunds

Items pass through the existing item categorization rules and remain editable. Editing an imported item preserves ASIN, brand, SKU, unit price and notes. A later Amazon sync also preserves manual categories and reviewed data if Amazon temporarily exposes less information.

Gift-card/account-balance parsing is deliberately conservative. Only explicit payment labels such as `Geschenkgutschein-Guthaben`, `Amazon-Guthaben` or `Gift Card balance` are accepted; a product whose name merely contains `Geschenkgutschein` is not treated as a payment. The user can correct the non-bank amount manually in the Amazon order detail. A manual correction is preserved across later syncs.

## Bank reconciliation

Amazon payment reconciliation is many-to-many:

- one order may be charged in multiple shipments/transactions;
- one Amazon bank transaction may cover multiple orders;
- each `PurchaseTransactionLinks` row stores the positive amount allocated from one bank transaction to one purchase;
- `Purchase.TransactionId` remains only the compatibility/primary link.

The normal automatic matcher:

- considers owned negative transactions from 3 days before through 21 days after the order date;
- prefers Amazon/AMZN counterparties;
- uses only the still-unallocated part of a bank transaction;
- tries exact combinations of up to 5 charges from the best 12 candidates;
- confirms an order only when allocated bank payments plus Amazon gift/account balance match the order total within 0.01 in the purchase currency.

For delayed shipments/pre-orders, FullWorth additionally checks up to 365 days after the order. In that wider window it automatically accepts only one unique exact Amazon charge; otherwise the user chooses manually.

After the individual-order pass, FullWorth detects the reverse split case: one larger Amazon bank charge that exactly equals the remaining amounts of 2–5 imported orders. The transaction is split automatically only when there is exactly one valid purchase combination. Ambiguous combinations remain unlinked for manual review.

The manual payment picker also supports the 365-day delayed-charge window. It shows both the full bank amount and the amount still available after allocations to other Amazon orders. The user chooses the amount allocated to the current order. The backend revalidates ownership, currency, date, available transaction amount and remaining order amount before saving.

Positive refund transactions are matched separately by currency, exact amount and date, preferring Amazon counterparties. If automatic refund matching is ambiguous or missing, the order detail offers a manual candidate picker. Manual refund links are revalidated server-side and can be removed again. A refund transaction is unique to one refund and cannot be reused as an Amazon purchase payment.

## Persistence

Migration `20260830150000_AddAmazonOrderSync` adds support tables without changing the existing `Purchase` schema:

- `AmazonConnections`
- `AmazonOrderMetadata`
- `PurchaseTransactionLinks`
- `PurchaseRefunds`

`AmazonOrderMetadata` stores external status plus detected/manual non-bank payment amount. `PurchaseTransactionLinks` stores `AllocatedAmount`, so the same transaction can participate in several purchases. Existing `Purchase.TransactionId` values are copied into allocation rows during migration without assuming that `TransactionId` was unique.

The support tables are intentionally accessed through `AmazonSqlStore` rather than added to the EF model so the existing Purchase model and migration snapshot remain compatible.

## Runtime

The backend package and runtime are pinned to Microsoft.Playwright `1.62.0`. The backend Docker runtime is `mcr.microsoft.com/playwright/dotnet:v1.62.0-noble`, which contains the matching Chromium build and Linux browser dependencies. No browser is downloaded at application startup.

Configuration defaults:

```json
"AmazonIntegration": {
  "Enabled": true,
  "InitialHistoryDays": 90,
  "MaxHistoryDays": 36500,
  "LoginChallengeMinutes": 10,
  "NavigationTimeoutSeconds": 45,
  "SyncIntervalHours": 24,
  "MaxOrdersPerSync": 5000
}
```

## Operational rules

- Never log Amazon credentials, OTPs or decrypted browser storage state.
- Never persist password or OTP.
- Never follow an order detail URL outside HTTPS `amazon.de` / `*.amazon.de`.
- Never attempt to solve or bypass CAPTCHA/anti-bot challenges.
- A parser failure stops the sync instead of silently dropping an order.
- Disconnect deletes the stored Amazon browser session but does not delete already imported purchases.
- Because the connector depends on Amazon's buyer website, selector/parser tests must be kept and the connector may require maintenance when Amazon changes its HTML.