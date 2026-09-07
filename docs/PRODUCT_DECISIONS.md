# FullWorth 1.0 product decisions

Status: accepted product decisions.

These decisions are the default contract for the 1.0 implementation. Change them deliberately rather than implicitly during feature work.

## Users, ownership and sharing

- Multi-user is required.
- Each user has an independent account/login.
- Financial accounts can be personal or shared.
- One financial account can have multiple owners.
- Access must be modeled explicitly; ownership is not inferred from an account ID or URL.
- All finance data belongs to a FullWorth Space/household and is additionally scoped by resource permissions where necessary.
- Shared-account membership uses an explicit join model so one or more users can own/access the same account.
- The initial MVP UI may operate against one implicit/default FullWorth Space without showing a visible Space switcher, but all application/API design must remain FullWorth-Space scoped so a later switcher does not require a data-model rewrite.

## Authentication and sessions

- Primary auth: secure first-party login with server-side cookie session.
- PWA support is required.
- Passkeys/WebAuthn are required as the preferred biometric unlock/sign-in option where the device/browser provides a platform authenticator (fingerprint/face/PIN).
- Password login remains available as recovery/fallback unless explicitly disabled by the user.
- Shipped: TOTP/Authenticator-app support is implemented — users can set up, enable and disable an authenticator app (`TwoFactorService`, `/auth/two-factor/*`), and the second factor is enforced on both password and external sign-in.
- Recovery codes and session/device revocation are required.
- The public browser never receives Backend, Banking or database credentials.

## Public deployment security

- The application must be safe for public HTTPS exposure.
- Only FullWorth.Web is intended to be publicly routed.
- FullWorth.Backend, FullWorth.Banking and PostgreSQL remain private/internal services.
- Every API read/write must authorize against the authenticated user and the requested FullWorth Space/resource membership.
- Never rely on unguessable UUIDs as access control.
- No global `GetById` path may return data before authorization/scoping is applied.
- Secure cookie settings, CSRF protection, session rotation, logout/revocation, rate limiting, security headers and audit logging are 1.0 requirements.
- Sensitive responses must use explicit DTOs; do not serialize persistence entities/raw provider payloads to clients by accident.

## Currency

- EUR is the base/default currency.
- Multi-currency data remains supported.
- Original transaction/account currency is preserved.
- Cross-currency totals/analytics use conversion rates and must surface an incomplete state when a required rate is unavailable; never silently assume 1:1.

## Bank ingestion

Initial target order:

1. DKB
2. ING
3. PayPal
4. C24
5. Revolut

- First import requests the maximum history practically/provider-available while respecting provider limits.
- Scheduled sync is throttled to a minimum interval of at least 6 hours of provider access per bank connection (`MinimumBackgroundSyncIntervalMinutes` = 360, persisted per connection as `nextSyncAllowedAt`). The background worker only wakes periodically (every 5–60 min) to check eligibility and never adds provider requests beyond that floor. (An earlier fixed three-slot morning/midday/evening scheduler, `BankSyncScheduleService`, still exists in the codebase but is not the path wired into the running worker.)
- Automatic sync must stay safely above the provider background-fetch floor.
- A manual sync is allowed only when the same safety/cooldown policy permits it.
- A manual sync shifts/skips an upcoming scheduled run if needed; automatic + manual runs must never stack into excess bank access.
- Pending transactions are displayed.
- When a provider later reports a pending transaction as booked/replaced/removed, local state is reconciled rather than duplicated.
- Raw provider transaction data is retained permanently unless the user later changes retention policy.
- Banking request safety invariants in `docs/BANKING_SAFETY.md` remain mandatory.

## Categories and categorization

- Hierarchical categories are required.
- Ship a rich set of useful default categories/subcategories.
- At initial FullWorth-Space setup, copy the default category template in the user's selected language into normal editable FullWorth-Space category records; do not keep seeded categories as immutable system objects.
- Changing UI language later does not automatically rename categories the user already owns.
- System/default categories are editable.
- Category hierarchy must not impose an artificial business-level nesting limit.
- Rule-based categorization is primary.
- Rules support transaction-level and purchase-item-level matching.
- AI may automatically suggest or change classifications when policy allows, but manual classifications are protected.
- If an AI/rule action would alter manually classified data, explicit confirmation is required.
- Bulk-change choices must include at least:
  - apply to this one item/transaction
  - apply to matching transactions for this account + counterparty/context
  - apply to all matching transactions globally within the accessible FullWorth Space
- When a user corrects a classification, offer to create/update a reusable rule.
- Transfers are auto-detected and always manually correctable.

## Transactions, transfers and refunds

- Manual accounts support manual transactions.
- Transaction splits are first-class allocations; statistical/category calculations must not double-count parent transaction plus split lines.
- Transfers are explicit relationships between transaction legs, not merely a normal expense category.
- Linked transfers are excluded from normal income/expense statistics by default and remain manually correctable.
- Transfers may have a separate purpose/savings classification without turning them into expenses.
- A per-transaction `exclude from statistics` concept remains independent of transfer status.
- Refunds/returns can link to the original transaction, split/purchase item and must reverse the relevant expense in analytics rather than becoming ordinary income by default.

## Budgets

- Support calendar-month budgets.
- Support salary/pay-cycle budgets.
- Support custom budget cycles.
- Use one universal budget model rather than separate incompatible budget-method implementations.
- Per-budget carry-over is configurable and defaults to off.
- Budget views include current spend, remaining amount, historical trend and forecast.
- Item-level purchase splits feed category budgets.
- Notifications are designed for multiple channels; first implementation is push notifications.

## Contracts, recurring costs and credit

- Automatically detected recurring payments become candidates and require confirmation before becoming contracts by default.
- Manual contract creation is required.
- Contracts have two value modes:
  - automatic: derive/refresh expected value from transaction history
  - manual: user owns the configured value
- Support monthly, quarterly, yearly and custom cycles.
- Support credits/loans as a first-class contract/liability type.
- Credit/loan fields include principal/current balance, start/current amount, payment, interest rate, dates and relevant fees where applicable.
- Credit/loan UI includes amortization graph, payoff estimate, total expected interest and principal/interest split.
- Full amortization calculations are required.

## Assets, investments and net worth

- Manual assets such as property are required.
- Manual asset values and historical value changes are supported.
- Net-worth history and forecast include accounts, assets and liabilities.
- Property valuation can be manual initially; external valuation remains an optional later adapter.
- Investment/portfolio UI is part of the MVP when security/position data is available, including whole-portfolio, asset-class and selected-security views.
- Dedicated ingestion adapters for unsupported brokers may be added later; the core data/UI model must not block them.
- Investment performance must distinguish realized/unrealized results and use mathematically appropriate return measures. TWR is the default portfolio performance percentage when sufficient history exists; MWR/XIRR is available for personal cash-flow-weighted return when sufficient dated cash flows exist.
- Buys, sells, deposits and withdrawals must not create artificial percentage-performance jumps.

## Purchases, receipts and extraction

- Receipt scanning is part of the PWA.
- Extraction architecture supports both OCR and AI.
- Providers must be pluggable so local and cloud extraction can coexist.
- Receipt files are retained initially.
- Receipt storage is persistent and covered by backup.
- A bank transaction can be broken into multiple purchases and line items.
- Each line item can have its own category.
- Reconciliation must prove/explain the difference between purchase/item totals and the linked bank transaction.
- Purchase UI supports receipt review, linking and analytics by merchant/product category. A full pre-shopping checklist/planner is deferred.

## Amazon

> ⚠ Needs decision: This section says not to depend on browser scraping and to prefer manual / e-mail / export-file adapters. The shipped connector does the opposite: it is the only implemented Amazon path and it drives Playwright/Chromium browser automation against the user's real Amazon account. The user enters their Amazon email/password (submitted to Amazon's sign-in form server-side; password/OTP are not persisted) and FullWorth stores the resulting encrypted browser session (`AmazonConnections.EncryptedStorageState`) and reuses it for automatic 24h syncs. This conflicts with the "no primary browser scraping" and manual/email/export intent. Product/security call needed: accept and document the Playwright + stored-session path as the sanctioned 1.0 strategy, or hold it back in favour of the manual/e-mail/export adapters below. See `docs/AMAZON_IMPORT.md`.

- Do not depend on browser scraping as the primary 1.0 strategy.
- Support manual import.
- Support e-mail/receipt/invoice based acquisition.
- Support export-file acquisition when Amazon exposes a usable customer export.
- Treat Amazon selling-partner/vendor APIs as unsuitable for ordinary personal buyer-order history unless Amazon provides a customer-authorized API for that use case.
- Model orders, individual items, multiple charges, deliveries and refunds independently of the acquisition adapter.
- Refunds/returns link back to the original order/item and corresponding finance transaction where possible.

## PWA and offline

- FullWorth.Web is an installable PWA.
- Offline mode does not cache sensitive finance datasets by default.
- Cache app shell/static assets only initially.
- Sensitive offline access can be revisited only with explicit encrypted-storage design.

## UI

- `docs/UI_UX_SPEC.md` is the binding detailed UI/UX implementation contract for the authenticated MVP application.
- Light, clear design remains the base visual direction.
- German and English at launch.
- Theme modes: System / Light / Dark.
- Additional visual themes and a Game Mode may be added later through semantic tokens/components without changing finance logic.
- Dashboard has a strong default layout and is configurable in the MVP.
- Desktop Dashboard uses a simple responsive grid; mobile defaults to an ordered full-width widget list.
- Users can use one shared dashboard configuration or separate desktop/mobile layouts.
- Analytics are extensive, not minimal, and saved analyses may become Dashboard widgets.
- Use both simple tables and richer data-grid behavior depending on the page/device.
- Responsive desktop/tablet/mobile behavior is required.
- Merchant/company logos may be shown for transactions/contracts when enabled; use category/account fallback icons and do not leak private transaction text through client-side third-party lookups.
- A global anonymized/privacy mode is required and must mask financial values consistently across pages/charts/tooltips. Shipped: an off/on privacy mode masks financial values via the shared money rendering path (`ui/privacy.js`).
- Future scope (not yet implemented): a stricter share/screenshot mode that additionally masks identifying/free-text data. Only the off/on value-masking mode ships today.
- The public marketing landing page is planned separately after the authenticated app experience has been implemented/tested.

## Notifications

- Push notifications are first.
- Architecture leaves room for e-mail and other channels later.

## Export and retention

- Export supports JSON and CSV.
- FullWorth transaction/history retention is permanent by default; no automatic deletion.
- Audit history is permanent by default.

## Backup

- Back up PostgreSQL and receipt/purchase files.
- Google Drive is a supported backup destination.
- Backup cadence is configurable; daily is the recommended default.
- Restore verification is a 1.0 release gate.

## External API / tool permissions

Future scope (not yet implemented): there is currently no external-tool grant/permission system in the code. The bullets below remain the target design and stay on the backlog. (Note: the existing per-user AI-provider credential storage and the internal service-to-service auth are separate concerns and do not satisfy this.)

- Follow least-privilege scopes rather than one universal API key.
- Separate read, write, banking/ingest and administration capabilities.
- External tools are read-only by default.
- A user can grant an external tool write access either once or persistently.
- Persistent grants are revocable and audited.
- Resource/FullWorth-Space scope must be part of every grant.

## 1.0 scope

- Target the full product described in the roadmap and the MVP UI contract in `docs/UI_UX_SPEC.md`.
- Shipped: Paperless receipt import is implemented and in scope (connection with encrypted API token, manual and automatic import presets, and a scheduled auto-import worker; `FullWorth.Backend/Modules/Purchases/ReceiptImports`).
- Investment/portfolio views are included when normalized investment data exists; unsupported broker connectivity can follow later without changing the core model.