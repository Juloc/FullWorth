# Banking

Reference for everything that connects FullWorth to a real bank: the two connectivity routes, how a
bank is chosen, the full authorization/TAN flows, the background sync worker, error handling, what the
UI shows, and what actually lands in PostgreSQL.

The request-safety invariants (cadence floors, PSU-header rules, rate-limit handling, consent-close
order) live in [Banking request safety](BANKING_SAFETY.md) and are not repeated here.

## The two routes

| | Enable Banking (PSD2/AIS) | FinTS 3.0 PIN/TAN |
|---|---|---|
| Provider value in `BankConnection.Provider` | `enable-banking` | `fints` |
| Banks | every ASPSP the user's Enable Banking application can reach | ING Germany only (`KnownBanks.Ing`) |
| Data | balances + transactions | balances + transactions + depot holdings |
| Credentials | per-user Enable Banking application (app id + RSA private key) | ING login + banking PIN, entered in FullWorth |
| Consent | bank redirect + provider session, `valid_until` | none; a fresh dialog per sync, TAN when the bank asks |
| History | whatever the ASPSP exposes (`strategy=longest`) | last 90 days, every sync |
| Code | `src/FullWorth.Banking/EnableBanking/`, `Services/BankSyncService.cs` | `src/FullWorth.FinTs/`, `Services/IngFinTsService.cs` |

Both run inside the one unified container (`FullWorthHost__Unified=true`); `BankingApplication.AddFullWorthBanking`
registers both services plus the `BankSyncWorker` hosted service in the same process as Web and Backend.
Banking's own API is mounted under `/api/banking` and is reachable only through the Web BFF
(`/bff/banking/**`), which attaches `X-FullWorth-Banking-Key`, the trusted `X-FullWorth-User-Id` /
`X-FullWorth-Space-Id` and the rebuilt PSU headers (`BankingUserContextHandler`). The three
`/connect/enable-banking/*` callbacks are intentionally anonymous.

Payment initiation is not implemented on either route. FinTS exposes no payment/order segments;
`KnownBanks.Ing` grants only `Accounts, Balances, Transactions, Portfolio, Tan, DecoupledTan`.

## Configuration

Read from the `EnableBanking`, `FinTs` and `Sync` sections (`src/FullWorth.Banking/appsettings.json`),
overridden by the `EnableBanking__*` / `FinTs__*` / `Sync__*` variables in `docker-compose.yml`.

| Key | Default | Effect |
|---|---|---|
| `EnableBanking:RedirectUrl` | empty | **required.** Compose derives `https://${FULLWORTH_DOMAIN}/connect/enable-banking/callback`. Without it `/connect` and profile verification fail. |
| `EnableBanking:BaseUrl` | `https://api.enablebanking.com` | AIS API |
| `EnableBanking:ControlPanelBaseUrl` | `https://enablebanking.com` | Control Panel (registration + bank-status feed) |
| `EnableBanking:ApplicationId`, `PrivateKeyPath`, `PrivateKeyBase64` | empty / `/run/secrets/enable-banking-private-key.pem` | legacy global credentials; used only by pre-BYO connections that have no `EnableBankingProfileId` |
| `EnableBanking:AuthorizationStateTtlMinutes` | 15 (clamped 1–60) | lifetime of the callback state |
| `EnableBanking:MinimumRequestSpacingMilliseconds` | 1000 (clamped 250–10000) | spacing between provider calls |
| `EnableBanking:TransientRetryCount` | 2 (clamped 0–3) | retries for 408/500/502/503/504 |
| `EnableBanking:ApplicationName`, `ApplicationDescription`, `PrivacyUrl`, `TermsUrl` | `FullWorth`, `Private finance web app`, `https://fullworth.de/privacy/`, `https://fullworth.de/terms/` | sent when FullWorth registers the application automatically |
| `FinTs:ProductId` | empty | **required for FinTS.** Not set means every ING connect returns HTTP 503 `fints_not_configured`. |
| `FinTs:HistoryDays` | 90 (clamped 1–90) | FinTS `HKKAZ` window |
| `FinTs:MaxPages` | 50 | FinTS touchdown page fuse |
| `Sync:IntervalMinutes` | 15 (clamped 5–60) | worker wake-up only |
| `Sync:MinimumBackgroundSyncIntervalMinutes` | 360 | per-connection background floor; code takes `max(360, value)`, so it can only be raised |
| `Sync:RateLimitCooldownMinutes` | 360 | cooldown after `ASPSP_RATE_LIMIT_EXCEEDED`, also floored at 360 |
| `Sync:OverlapDays` | 7 | Enable Banking incremental re-read window |
| `Sync:PersistBatchSize` | 250 (min 25) | transactions per ingest call |
| `Sync:MaxPagesPerAccount` | 250 | history page fuse; not exposed as a compose variable |

`Security:ApiKey` (banking API key) and `Backend:IngestKey` are required at startup by
`InitializeFullWorthBanking`.

## Enable Banking: bring-your-own application

An Enable Banking restricted-production application may only access accounts linked to that
application, and personal production use is meant for the individual who owns those accounts.
FullWorth therefore never shares one application between users: each user supplies their own
(`EnableBankingProfile`, one row per `UserId`). A hosted, shared banking offering would need a
production agreement with Enable Banking first.

`EnableBankingProfile` stores `ApplicationId`, the RSA `PrivateKeyPem`, an optional Control Panel
`ControlPanelRefreshToken`, `KeyFingerprint`, `Environment`, `ApplicationName`, `Active`,
`ServicesJson`, `RedirectUrlsJson`, `VerifiedAt`. The key and the refresh token are encrypted with
`FieldCipher` and are only ever returned over the ingest-key-protected internal API — never to a
browser (`EnableBankingProfileView` omits them and exposes the fingerprint instead).

### Setup wizard (Settings → Enable Banking)

`GET /api/banking/status` returns `{ configured, legacyConfigured, callbackUrl, profile }`. The
frontend treats a profile as usable when `environment === 'SANDBOX'` or `active === true`
(`bankingReady` in `features/accounts.js`).

Automatic path (`EnableBankingControlPanelRegistrationService`):

1. `POST /api/banking/profile/register/start` with email + `SANDBOX|PRODUCTION`.
2. FullWorth generates a 4096-bit RSA key pair in-process and requests a passwordless Control Panel
   email link (`POST /api/relyingparty/getOobConfirmationCode`) pointing at
   `/connect/enable-banking/setup-callback?state=<id>`.
3. The email link hits that callback; `emailLinkSignin` is exchanged for an ID token (kept in memory
   only) and a refresh token, then `POST /api/applications` registers the application with the public
   key and the FullWorth callback. `PRODUCTION` additionally sends description, GDPR email, privacy and
   terms URLs.
4. The generated private key is verified with `GET /application` and only then persisted encrypted,
   together with the refresh token.
5. The wizard polls `GET /api/banking/profile/register/{id}` every 1.5 s until `completed`, `failed`
   or `expired`.

Pending registrations are user-bound, single-use, expire after 20 minutes and live **only in the
banking service's memory** (`ConcurrentDictionary`). A restart during the email window loses them.
The Control Panel sign-in endpoints are not part of Enable Banking's public API reference, so this
path is a beta integration; the registration flow has no fallback when Enable Banking rejects the
FullWorth domain as an email-link `continueUrl` — it fails with `control_panel_login_start_failed`
(HTTP 502) and the user must use the manual path.

Manual path: create the application in the Control Panel, add the shown callback URL, upload/generate
a key, then `POST /api/banking/profile/verify` with the application id and the PEM. `ValidateApplication`
requires all of: `kid` equals the supplied application id, environment `SANDBOX` or `PRODUCTION`,
`services` contains `AIS`, and `redirect_urls` contains the configured `EnableBanking:RedirectUrl`.
The uploaded PEM must carry private parameters (a public key is rejected as a 400).
`active=false` on a production application is not an error: the wizard shows the linked-accounts
activation instructions and `POST /api/banking/profile/recheck` re-runs `GET /application`.

`DELETE /api/banking/profile` returns 409 `profile_in_use` while connections still reference it.

### Bank status feed (optional)

`GET /api/banking/provider-status?country=DE` reads the same Control Panel endpoint as Enable Banking's
official CLI (`GET /api/get_today_stats`) and returns `{ available, reason, checkedAt, statuses[] }`.
It needs a stored `ControlPanelRefreshToken`, so it works out of the box only for profiles created
through the automatic path. Older/manual profiles opt in once via
`POST /api/banking/provider-status/connect/start` → email link → `/connect/enable-banking/status-callback`,
which stores the refresh token without touching the existing application. When Enable Banking refuses
the FullWorth domain as a `continueUrl`, this flow falls back to the CLI's `http://localhost:8888/`
continue URL and returns `manualCompletionRequired: true`; the user pastes the resulting localhost URL
into the dialog and `POST /api/banking/provider-status/connect/complete` extracts the `oobCode`.
ID tokens are cached in memory per user; the refresh token stays encrypted in the profile.

## Choosing a bank

`openBankDialog` (`features/accounts.js`) drives the picker:

- Country input (2 letters, default `DE`) and a client-side name filter, max 100 rows drawn.
- For `DE` a synthetic first row is injected client-side: `{ name: 'ING', group: 'FinTS', fullworthProvider: 'fints' }`.
  It is the only entry shown when no Enable Banking profile is ready, and the Enable Banking ING entry
  is filtered out of the list so ING appears exactly once.
- Everything else comes from `GET /api/banking/institutions?country=XX`, i.e. Enable Banking
  `GET /aspsps?country=XX&service=AIS`, resolved through the caller's own profile
  (`EnableBankingClientResolver.ResolveForUserAsync`, `requireActive: true`).
- `mergeBankOptions` collapses same-name/same-country ASPSP variants into one row and unions their
  `psu_types` and `auth_methods`, so a bank with separate personal/business entries is not listed twice.
- Each row shows logo, country, group, PSU types, a `beta` marker and — when the status feed is
  available — a severity chip derived from the Enable Banking status text (`major` / `possible` /
  `ok` / `unknown`). The severity is repeated as a warning block in the connect dialog.
- Selecting the FinTS row opens the ING dialog; anything else opens `openBankConnectionOptions`.

`openBankConnectionOptions` lets the user pick the PSU type (personal first, business shows an extra
notice), optionally pick a visible `auth_method` (hidden methods are dropped) and fill that method's
documented credential fields (`template` becomes the input `pattern`, password-ish names become
password inputs). An advanced checkbox allows restricting `access.accounts` by pasting IBANs or
`SCHEME|identification|issuer` lines; by default `access.accounts` is omitted so the bank's own consent
UI chooses.

## Enable Banking: authorization, session, sync

### Start (`POST /api/banking/connect`)

`BankSyncService.StartConnectionAsync`:

1. `backend.AuthorizeAsync` checks the caller owns the space (and the reconnect target / requested
   profile). Not authorized → 403/404.
2. Reconnect (`reconnectConnectionId`) reuses the existing row and its profile id.
3. The ASPSP list is refetched and the institution matched by name; PSU type must be in its
   `psu_types`; `auth_method` and credentials are validated against the provider schema (unknown field,
   empty value, failing `template`, credentials without a method → 400/409). Credentials are request
   data only and are never stored or logged.
4. `valid_until` = now + `min(validDays (default 365, clamped 1–365), maximum_consent_validity)`;
   when the ASPSP publishes no maximum, 90 days is assumed.
5. `state` = 32 random bytes hex, expiring after `AuthorizationStateTtlMinutes`, unique-indexed and
   single-use. `psu_id` = `sha256(applicationId|userId)` — stable, no email/name.
6. `POST /auth` with `access.balances=true`, `access.transactions=true`, `valid_until`, `aspsp{name,country}`,
   `state`, `redirect_url`, `psu_type`, optional `auth_method` / `credentials` / `credentials_autosubmit`
   / `language` (two lowercase letters) / `access.accounts`.
7. The row is upserted with the state, `authorization_id`, the ASPSP's `required_psu_headers` (as
   jsonb) and status `PENDING_AUTHORIZATION` — **except on reconnect, where the previous status,
   `validUntil`, timestamps and session id are preserved** so abandoning the bank flow cannot downgrade
   a working connection.
8. The browser is sent to the returned provider URL.

### Callback (`GET /connect/enable-banking/callback`)

- Provider error → `HandleAuthorizationErrorAsync` consumes the state; for a brand-new connection the
  row becomes `CANCELLED` (`access_denied`/anything containing "cancel") or `INVALID`, with
  `AUTHORIZATION_CANCELLED` / `AUTHORIZATION_FAILED`. A staged reconnect is left untouched. The browser
  is redirected to `/?bankError=<code>[&bankErrorDescription=…]` with control characters stripped and
  the values truncated (64 / 180).
- Missing `code`/`state` → `/?bankError=app_missing_parameters`.
- Success → `CompleteConnectionAsync`: consume state, `POST /sessions {code}`, require `session_id`
  and a parsable `access.valid_until` (a malformed session is closed best-effort with `DELETE /sessions`
  and the connection becomes `INVALID`/`AUTHORIZATION_FAILED` — but only when it had no session before;
  a failed reconnect keeps the old session and status). The row is then stored `AUTHORIZED` with the
  encrypted session id, and a replaced old session is closed best-effort (404/410 counts as success).
  Finally the initial sync runs inside the concurrency gate, with the callback's PSU context.
- The redirect is `/?bankConnected=<institution>` unless the resulting status is terminal
  (`EXPIRED|REVOKED|CLOSED|INVALID|CANCELLED`), which redirects to `bankError=reauthorization_required`.

### Session status

Every sync first reads `GET /sessions/{id}`. Any status other than `AUTHORIZED` is stored verbatim
(unknown provider values included) with a mapped error code — `EXPIRED`→`SESSION_EXPIRED`,
`REVOKED`→`SESSION_REVOKED`, `CLOSED`→`SESSION_CLOSED`, `CANCELLED`→`AUTHORIZATION_CANCELLED`,
`INVALID`→`AUTHORIZATION_FAILED` — and terminal statuses clear `NextSyncAllowedAt` so the worker stops
retrying.

### Account discovery and identity

Accounts come from the session's `accounts_data` (objects) plus `accounts` (objects or bare uid
strings), de-duplicated by uid. `identification_hash` is the durable identity; `identification_hashes`
are kept as fuzzy aliases; the session-scoped `uid` is only the API path segment
(`ProviderAccountId`) and never an identity.

- A bare uid gets a placeholder hash `uid:<uid>` and must be resolved through
  `GET /accounts/{uid}/details`. No resolvable `identification_hash` → the account is **skipped** and
  the connection ends with `ACCOUNT_RESOLUTION_FAILED`.
- If no sync state exists for any known hash, `/details` is fetched once so a renamed primary hash can
  be matched through its aliases before FullWorth decides the account is new (which would trigger
  another full-history import). Ongoing syncs do not refetch `/details`; a 404 is tolerated and the
  session metadata is used.
- Ingest promotes the provider's current primary hash and keeps the previous one as an alias. It
  refuses to steal a primary hash owned by another account and throws on ambiguous multi-account
  matches rather than merging.

### Transactions and balances

`GET /accounts/{uid}/balances` per account, then paged `GET /accounts/{uid}/transactions`:

- First import (no stored BOOK booking date): `strategy=longest`, no `date_from`, no `date_to`.
- Later: `strategy=default`, `date_from` = latest stored BOOK booking date − `OverlapDays`,
  `date_to` = today. A `date_from` in the future is pulled back to today − overlap.
- Pages follow `continuation_key` until it is absent, including through empty pages, up to
  `MaxPagesPerAccount`; hitting the fuse records `HISTORY_PAGE_LIMIT_REACHED` and does **not** advance
  `LastSyncedAt`.
- `WRONG_TRANSACTIONS_PERIOD` on the first page of an incremental run with `date_from` older than 90
  days narrows once to 90 days and retries a single time; it never loops on the rejected period.
- `EnableBankingClient` supports the `transaction_status` filter for all documented values
  (`BOOK CNCL HOLD OTHR PDNG RJCT SCHD`, unknown values rejected before the request), but no sync path
  and no UI passes one today — every sync fetches the provider default.

Sign and text normalisation: `DBIT` → negative, `CRDT` → positive; counterparty is the creditor for a
debit and the debtor for a credit (with the other side as fallback); description is
`remittance_information` joined with ` | `, else `note`, else the bank transaction code description.

Transaction identity:

- `entry_reference` present → `ExternalKey = "er:<entry_reference>"`.
- otherwise → `ExternalKey = "fp:<sha256(accountHash|status|booking|value|amount|currency|counterparty|description)>"`,
  which is account- and status-scoped, so a pending row is never fused with a booked one.
- `transaction_id` is stored as `ProviderTransactionId` (a details pointer) and is never an identity.
- The unique key is `(AccountId, ExternalKey)`. On ingest, an older `transaction_id`-keyed row may be
  adopted only when the incoming key is exactly `er:<entry_reference>` and exactly one stored row
  carries that entry reference; ambiguous historical rows are left alone.

### Transaction details

`GET /api/banking/transactions/{id}/details` resolves the pointer through the backend (space
membership **and** account ownership required), then calls
`GET /accounts/{uid}/transactions/{transaction_id}` with online PSU context and returns a normalised,
safe view (status, dates, amount, indicator, creditor/debtor names, counterparty account last 4,
remittance lines, MCC, bank transaction code). Requires `ProviderTransactionId`, an `AUTHORIZED`
connection and a live session. Consent/auth/rate-limit failures update the connection health; a
one-off detail failure does not.

### Disconnect

`DELETE /api/banking/connections/{id}?deleteLocalData=true|false`:

1. For `enable-banking` with a session: `DELETE /sessions/{id}` first. 404/410 is an idempotent
   success. Another provider error aborts with 502 `ProviderFailed`, unless the connection is already
   terminal or the error classifies as consent-expired — then the local disconnect continues.
   FinTS connections skip this step entirely.
2. `deleteLocalData=false` (the UI default) → `close-retain`: status `CLOSED`, and
   `AuthorizationState`, `AuthorizationId`, `ProviderSessionId`, `NextSyncAllowedAt`, `LastError` are
   cleared, so the stored credentials/session are dropped but all data stays.
3. `deleteLocalData=true` → the connection, its accounts, their balance snapshots and transactions are
   deleted; contracts and loans are detached from the account, receipts (purchases) are unlinked but
   kept, price-change suggestions built from those transactions are removed. Irreversible.

There is deliberately no browser-reachable backend route that deletes a connection without going
through this remote-consent-close path, and no browser-facing "sync everything" endpoint.

## FinTS (ING)

`FullWorth.FinTs` is an owned FinTS 3.0 implementation with no third-party FinTS dependency: wire codec
(`FinTsWire`), message builder (`FinTsMessages`), response/segment parser (`FinTsResponse`), HTTPS
transport (`FinTsTransport`, base64 body, HTTPS enforced) and one bank profile
(`KnownBanks.Ing`: BLZ `50010517`, BIC `INGDDEFFXXX`, `https://fints.ing.de/fints/`).

Messages are PIN/TAN-enveloped: `HNVSK` encryption head, `HNSHK` signature head, business segments,
`HNSHA` signature footer carrying the PIN (and TAN when one is being submitted), all wrapped in `HNVSD`
inside `HNHBK`/`HNHBS`. FinTS request and response payloads are never logged, because `HNSHA` contains
the PIN/TAN.

### Connect (`POST /api/banking/fints/ing/connect`)

Body: `{ userId, pin, tanMedium?, reconnectConnectionId? }` — the UI never sends `tanMedium`.

1. Space authorization, then `FinTs:ProductId` must be configured (else 503 `fints_not_configured`).
2. `SynchronizeAsync`: `HKIDN` + `HKVVB` + `HKSYN` in a dialog with system id `0`, which returns the
   BPD/UPD versions, the system id (`HISYN`), the account list (`HIUPD`), the per-segment TAN
   requirement (`HIPINS`) and the TAN methods (`HITANS`). The sync dialog is then ended best-effort.
3. `OpenAsync`: a real dialog with the discovered system id, plus `HKTAN` process 4 referencing `HKIDN`
   when the bank has TAN methods. The security function is chosen from the `3920` allowed list,
   preferring a decoupled method.
4. The whole state — bank id, login, PIN, product id, discovered `FinTsBankParameters`, the open
   session and any pending challenge — is serialised to JSON into `BankConnection.AuthorizationId`,
   which the backend stores encrypted (`FieldCipher`). `ProviderSessionId` is a synthetic
   `fints:ing:<spaceId>:<sha256(login)>` key (unique per space+login), status `AUTHORIZED` or
   `TAN_REQUIRED`, `NextSyncAllowedAt` = now + ≥360 min.
5. If the dialog opened without a TAN, the initial sync runs immediately (cadence bypassed) and the
   response reports the counted cash accounts and depots.

### TAN and challenge handling

`FinTsResult`/`FinTsOpenResult` carry one of four kinds: `Success`, `TanRequired` (interactive TAN),
`TanPending` (decoupled/push approval) or `Empty`. A challenge is `HITAN`-derived
(`TaskReference`, `Challenge` text, `IsDecoupled`, optional `HhdUc` binary). Response codes drive it:
`0030`/`3955` mean a TAN is required, `3955`/`3956` mean a decoupled approval is still pending, `3076`
means SCA-exempt (no TAN despite the code), `3040` carries the touchdown/continuation token.

A TAN can be demanded at dialog open or by a business segment (`HKSAL`, `HKKAZ`, `HKWPD` get a
`HKTAN` process 4 companion when `HIPINS` says so). A business-segment TAN raises an internal
interactive-required signal that stores the session + challenge on the connection and flips it to
`TAN_REQUIRED`, `LastError = FINTS_TAN_REQUIRED`, `NextSyncAllowedAt` cleared.

Continuation endpoints, both scoped to the stored challenge:

- `POST /api/banking/fints/connections/{id}/tan` with `{ tan }` → `HKTAN` process 2.
- `POST /api/banking/fints/connections/{id}/poll` → `HKTAN` process `S`; 409 if the challenge is not
  decoupled.

On success the challenge/session are cleared from the secret, the status returns to `AUTHORIZED` and
the full sync is re-run from a fresh dialog. `FinTsTanMethod` also carries the bank's poll hints
(`MaxPolls`, `WaitBeforeFirstPollSeconds`, `WaitBeforeNextPollSeconds`) — the UI does not use them; the
decoupled dialog only offers a manual "check again" button.

Only ING error codes `9942`/`9340` (wrong PIN) and `9930`/`9931` (access locked) are treated as
terminal: the connection becomes `INVALID` with its cooldown cleared. Every other FinTS error keeps the
current status, sets `FINTS_<code>` and schedules the next attempt after the cooldown.

### FinTS sync

One dialog per sync, then per account from `HIUPD` (accounts without an IBAN are skipped):

- Cash account: `HKSAL` balance, then `HKKAZ` from `today − FinTs:HistoryDays` to today, following the
  `3040` touchdown up to `FinTs:MaxPages` pages. FinTS sync ignores the stored latest booking date and
  `Sync:OverlapDays` — it re-reads the same 90-day window every time.
- Depot (`HIUPD` product name contains "Depot"): `HKWPD` holdings, same touchdown paging.
- `HKEND` closes the dialog, the refreshed parameters are written back, status `AUTHORIZED`,
  `LastSyncedAt` set, failures reset.

MT940 parsing (`:61:`/`:86:`) yields booking date, value date, signed amount, currency from `:60F/M:`,
counterparty from `?32`/`?33` and the joined `:86:` text. Booked and pending statements are parsed
separately (`HIKAZ` field 1 = booked, field 2 = pending). Each transaction's key is
`sha256(booking|value|amount|currency|MT940 rest|description|pending)`, stored as
`ExternalKey = "fints:<hash>"` with the same hash duplicated into `ProviderTransactionId` and
`EntryReference`. Because the pending flag is part of the hash, a pending booking and its later booked
form are two different rows — FinTS has no pending→booked reconciliation.

Depot holdings go through `POST /internal/banking/fints/investment-snapshot` (raw SQL,
`src/FullWorth.Backend/Modules/Ingestion/FinTsInvestmentSnapshotEndpoints.cs`): an
`InvestmentPortfolios` row named `fints:<connectionId>:<depotKey>`, a `Securities` row per holding
matched by ISIN or provider key, a `SecurityPrices` row (source `fints`) when a price is present, and
one `InvestmentTrades` row per holding with `TradeType = security_transfer_in`,
`Source = fints_snapshot`, `ExternalKey = fints-position:<providerKey>`. Positions no longer present in
the snapshot are deleted; holdings with quantity ≤ 0 are ignored.

## Background sync worker

`BankSyncWorker` waits for application start, then loops: `BankSyncService.SyncAllAsync`, log, sleep
`Sync:IntervalMinutes`. `SyncAllAsync` takes the process-wide `BankSyncConcurrencyGate` non-blockingly
(a manual sync in flight makes the whole scheduled pass a no-op, reported as `alreadyRunning`), then
considers only connections that are `AUTHORIZED`, have a session id and are not past `ValidUntil`.
`CanBackgroundSync` additionally requires `NextSyncAllowedAt` to be in the past **and**
`LastAttemptAt + max(360 min, configured floor)` to have elapsed. Background runs send no PSU headers.

`Services/Scheduling/BankSyncScheduleService.cs` (three daily local slots, DST-safe) is **not wired
into anything** — it is registered nowhere and only exercised by `tests/FullWorth.Banking.Tests/Scheduling`.
The live cadence is purely the interval + floor above.

A manual sync (`POST /api/banking/connections/{id}/sync`, the UI always sends `force=true`) requires
`AUTHORIZED` + session + unexpired consent (else `reauthorization_required`), may bypass the 6-hour
cadence, but is refused with `cooldown` while `NextSyncAllowedAt` is in the future and the last error
was `ASPSP_RATE_LIMIT_EXCEEDED`. The cooldown is re-checked after taking the gate. Results map to
`completed | partial_history | error | cooldown | already_running | reauthorization_required`.

Each attempt is recorded as a `bank_sync.attempt` audit event (start, end, `success|partial|error`,
error code) — there is no separate sync-history table. History writes are best-effort: a failure to
persist history never turns a completed retrieval into a failed sync.

## Error handling and timeouts

`EnableBankingErrorClassifier` maps provider failures onto stable, non-secret codes, and raw provider
bodies are never surfaced as user messages:

| Category | Code | Consequence |
|---|---|---|
| RateLimit (429 or `ASPSP_RATE_LIMIT_EXCEEDED`) | `ASPSP_RATE_LIMIT_EXCEEDED` | cooldown = `max(Retry-After, max(360, RateLimitCooldownMinutes))`, never bypassable |
| PsuContext | `PSU_HEADER_NOT_PROVIDED` | 409, connection health unchanged for detail calls |
| TransactionsPeriod | `WRONG_TRANSACTIONS_PERIOD` | single narrowed retry |
| ConsentExpired (code contains CONSENT/SESSION/EXPIRED…) | `SESSION_EXPIRED` / `SESSION_REVOKED` / `SESSION_CLOSED` | status → `EXPIRED`/`REVOKED`/`CLOSED`, cooldown cleared, reauth required |
| AuthRequired | `AUTHORIZATION_FAILED` | status → `INVALID`, reauth required |
| ApplicationAuth (bare 401/403) | `ENABLE_BANKING_AUTH_FAILED` | 409; connection identity kept, **not** treated as consent expiry |
| TransientProvider (408/500/502/503/504) | `PROVIDER_UNAVAILABLE` | 503 to the browser, retried later |
| InvalidRequest (400/404/422) | sanitised provider code or `PROVIDER_REQUEST_REJECTED` | 502 |
| anything else | `SYNC_FAILED` | 502 |

FinTS codes are `FINTS_SECRET_MISSING`, `FINTS_TAN_REQUIRED`, `FINTS_SYNC_FAILED`,
`FINTS_NOT_CONFIGURED` (dispatch reached a FinTS row without the FinTS service) and
`FINTS_<bank code>`. Partial Enable Banking outcomes produce `ACCOUNT_RESOLUTION_FAILED` or
`HISTORY_PAGE_LIMIT_REACHED`, both of which keep the previous `LastSyncedAt`.

Retry backoff (policy in [Banking request safety](BANKING_SAFETY.md)): `2^(attempt+1)` seconds plus
jitter, or `Retry-After` when the response carries one, capped at 30 s.
`EnableBankingRequestPolicy` is a singleton and the lease is held for the whole round trip, so **all**
Enable Banking AIS calls in the process are serialised with at least `MinimumRequestSpacingMilliseconds`
between them, across users. Control Panel calls (registration, bank status) do not go through that gate.

Timeouts: Enable Banking AIS and Control Panel clients 90 s; FinTS client 5 min; banking→backend client
5 min; the Web→banking BFF proxy 5 min. The browser has no client-side timeout, so a long FinTS connect
simply keeps the dialog's submit button disabled until the server answers.

## What the UI shows

`BankConnectionStatusView` (`GET /api/bank-connections`) exposes id, space, provider, institution,
country, PSU type, status, `validUntil`, `lastSyncedAt`, `nextSyncAllowedAt`, `updatedAt`,
`healthStatus`, `daysUntilExpiry` — never a session id, authorization id or FinTS secret. All members
of the space see the connections; only owners can act on them and only owners get notifications.

`BankConnectionConsentHealthCalculator` produces exactly nine health values, in this precedence:

| health | when |
|---|---|
| `expired` / `revoked` / `closed` | status says so, or `validUntil` is in the past |
| `reauthorization_required` | any other non-`AUTHORIZED` status, or no session id (covers `PENDING_AUTHORIZATION` and FinTS `TAN_REQUIRED`) |
| `partial_history` | `LastError = HISTORY_PAGE_LIMIT_REACHED` |
| `error` | any failures or any other `LastError` |
| `cooldown` | `nextSyncAllowedAt` in the future |
| `expiring` | consent ends within 7 days |
| `authorized` | otherwise |

The Accounts view renders per connection: institution, `validUntil`, `lastSyncedAt`, days-until-expiry,
next allowed sync, the translated health label (red for `reauthorization_required`, `expired`,
`revoked`, `closed`, `error`, `partial_history`), a **Sync history** button, then either **Reconnect**
(warning healths) or a **⟳ sync now** icon (healthy only), plus **Disconnect**. The raw error code is
not shown on the row — only inside the sync-history dialog, which lists the last 10 attempts with
result, error code, start/end and duration.

Owner notifications fire on edges only: `BankSyncError` on the first failure after a clean state and
`BankReauth` when health enters `reauthorization_required`/`expired`/`revoked`/`closed` — not on every
failed poll.

Accounts show `displayName` (from `details`, else `product`), institution, product/type, masked IBAN
last 4, and — for the displayed balance — its as-of date ("Datenstand", the provider's `reference_date`)
or, when there is none, the time it was fetched ("Abgerufen"). When several balance types were captured,
the displayed one is picked deterministically: `interimAvailable` > `closingAvailable` > `closingBooked` >
`interimBooked` > `expected`, anything unrecognised last. The row also says WHAT the figure is —
`meaning` (`available`/`booked`/`expected`/`recorded`, derived from the balance type by
`CurrentBalances.Meaning`) is rendered as one muted word under the amount, because available and booked
differ by exactly the pending authorisations a reader is trying to reconcile.

## Failure modes that look like nothing happened

These are the states where a real failure is invisible or misleading in the UI. They are the reason
the sync-history dialog exists.

1. **A FinTS TAN demanded during a background or manual sync cannot be answered from the UI.**
   The connection flips to `TAN_REQUIRED` and the stored challenge waits, but the only affordance for a
   warning health is **Reconnect**, and `reconnectConnection` never looks at `connection.provider`. It
   fetches `/api/banking/status` and, with no Enable Banking profile, opens the *Enable Banking* setup
   wizard; with a profile it looks the institution name up in the Enable Banking ASPSP list and — when
   found, which "ING" is — starts an *Enable Banking* authorization carrying the FinTS connection id,
   which rewrites `Provider` to `enable-banking`. Only when the name is not found does it fall back to
   the full picker, where the ING/FinTS row would actually resume FinTS. The `BankReauth` notification
   points at the same button. In practice the pending FinTS challenge is only reachable through
   Add bank → ING with the login and PIN re-entered.
2. **A manual sync that ends in a FinTS TAN reports a generic error.** `RequestManualSyncAsync` sees
   `LastError = FINTS_TAN_REQUIRED` and returns `error`, so the toast is the generic sync-error text —
   nothing tells the user a TAN is waiting.
3. **A successful authorization whose initial sync fails still toasts "connected".** The callback
   deliberately never rolls back a valid authorization; the sync exception is only logged. The user
   sees `bankConnected=<bank>` and a connection with no accounts. The failure is visible only as the
   connection's health/error and in the sync history.
4. **A skipped account is silent per account.** An account whose `identification_hash` cannot be
   resolved is skipped with a log line; the connection just ends in `error` /
   `ACCOUNT_RESOLUTION_FAILED` with no indication of *which* account is missing.
5. **`HISTORY_PAGE_LIMIT_REACHED` keeps the old `lastSyncedAt`.** The row shows `partial_history` and a
   stale successful-sync timestamp; the account looks fully synced apart from that label.
6. **A lost automatic-registration state polls forever.** The pending registration lives only in
   memory, so a restart (or a 20-minute expiry followed by a 404) makes
   `GET /api/banking/profile/register/{id}` return 404; the wizard writes the error into the status line
   and keeps polling every 1.5 s without ever failing the step. The user has to close the dialog and
   start again.
7. **A scheduled pass can be a complete no-op.** `SyncAllAsync` takes the gate non-blockingly, so if
   any other sync holds it the whole pass is skipped; the only trace is the `alreadyRunning=true` log
   line.
8. **"Bankdetails" is offered on transactions that can never have provider details.** The button
   renders for every non-manual transaction. For FinTS transactions it returns 409 with an explanatory
   message; for imported (`finanzguru-import`) accounts the pointer lookup 404s and the toast shows the
   bare string `404`.
9. **Disconnecting a FinTS connection with "delete data" leaves the depot behind.** The delete path
   removes accounts, balance snapshots and transactions, but nothing removes the
   `InvestmentPortfolios` / `InvestmentTrades` / `Securities` rows created from `HKWPD`.

## What lands in the database

`bank_connections` (`BankConnection`): provider, institution, country, PSU type, auth method,
`RequiredPsuHeadersJson` (jsonb), status, `ValidUntil`, `LastAttemptAt`, `LastSyncedAt`,
`NextSyncAllowedAt`, `ConsecutiveFailures`, `LastError` (≤2000 chars, always a FullWorth code),
`FullWorthSpaceId`, `AuthorizationUserId`, `EnableBankingProfileId`. `AuthorizationState` is
unique-indexed and consumed atomically. `ProviderSessionId` and `AuthorizationId` are encrypted
(`FieldCipher`) with a blind-index lookup column, and `(Provider, ProviderSessionIdLookup)` is unique.

`accounts` (`FinanceAccount`), unique on `(FullWorthSpaceId, Provider, IdentificationHash)`:
`BankConnectionId`, `Provider`, `IdentificationHash` + `IdentificationHashesJson` aliases,
`ProviderAccountId` (the provider uid / `fints:<hash>`), `InstitutionName`, `DisplayName`, `Product`,
`AccountType`, `Usage`, `PsuStatus`, `CreditLimitAmount`/`Currency`, `Currency`, `IbanLast4`,
`IbanLookup`. **The full IBAN is never stored** — only the last 4 and a keyed lookup token used for
exact transfer matching. Account-holder names, addresses and legal-age data from
`AccountResource` are deliberately not persisted. A batch marked `HasDetails=false` may seed a new
account but never overwrites existing real metadata, and display names the user has edited are not
overwritten. The first ingest also grants `AccountOwner` to the authorizing user when that user is a
space owner and the account has no owner yet.

`balance_snapshots` (`BalanceSnapshot`): append-only `Amount`, `Currency`, `BalanceType` (the provider's
own value — Enable Banking may deliver several per sync; FinTS always writes `closingBooked`),
`ReferenceDate`, `CapturedAt`. All balance types from one sync share the same `CapturedAt`.

`transactions` (`FinanceTransaction`), unique on `(AccountId, ExternalKey)`: `ExternalKey`,
`ProviderTransactionId`, `Status` (`BOOK`/`PDNG`/… as delivered), `BookingDate`, `ValueDate`, `Amount`
(signed), `Currency`, `Counterparty` + `NormalizedCounterparty`, `Description`, `MerchantCategoryCode`,
`EntryReference`, `CounterpartyAccountLookup` (keyed token, never a plaintext IBAN), `RawJson`
(the full provider payload, encrypted at rest; for FinTS `{source:"MT940", raw:<statement>}`),
`FirstSeenAt`, `UpdatedAt`. Every ingest re-runs categorisation unless
`CategorizationSource = "manual"`, and re-runs exact transfer detection for the space. New `PDNG` rows
and every observed status change are written to the audit log with the time FullWorth saw them.

`enable_banking_profiles` (`EnableBankingProfile`): one per user, private key and Control Panel refresh
token encrypted.

Investments from FinTS depots: `InvestmentPortfolios`, `Securities`, `SecurityPrices`,
`InvestmentTrades` as described above.

Cross-provider note: the account uniqueness key includes `Provider`, so the same ING Girokonto
connected through both FinTS and Enable Banking becomes **two** accounts with two transaction sets.
There is no cross-provider reconciliation — `FinanzguruAccountReconciliationService` only links
`finanzguru-import` accounts to real ones.

## Automated coverage

- `tests/FullWorth.Banking.Tests` covers the Enable Banking side broadly: AIS contract shape, JWT/client
  safety, session shape, callback handling, connect tenancy, manual sync, disconnect, sync flow and
  safety, error classification, profile service, client resolver, Control Panel registration and status
  (including the fallback), transaction details, API-key gating, not-configured behaviour and the
  unused schedule service.
- `tests/FullWorth.FinTs.Tests` has five tests: wire round-trip, message envelope sizing, the ING
  profile's read-only capability set, `HNVSD` unwrapping + balance parsing, and MT940 booking parsing.
- There are **no** tests for `IngFinTsService`, the ING connect/TAN/poll endpoints or the FinTS depot
  snapshot ingest. `tests/FullWorth.Web.Tests/AccountsUxBaselineTests` only asserts that the frontend
  still calls `api/banking/fints/ing/connect`.
- There is no linter, no formatter and no automated browser test anywhere in the repo, so the picker,
  wizard and TAN dialogs are only ever verified by hand.

## Live bank validation

Operator-run, against a deployed instance with the user's own accounts. This is read-only validation:
never run load or destructive testing against a bank or against Enable Banking, respect the 360-minute
background floor and provider cooldowns, and capture screenshots/logs without RSA keys, JWTs or session
ids. Run the banks in this order — DKB (booked/pending SEPA baseline), ING (second bank, connection
isolation), PayPal (many small transactions, odd counterparties), C24, Revolut (multi-currency, heavy
pending churn).

Prerequisites: HTTPS reachable at the configured domain; `EnableBanking__RedirectUrl` resolving to
`…/connect/enable-banking/callback` (compose builds it from `FULLWORTH_DOMAIN`); the Enable Banking
profile set up through either wizard path and, for restricted production, `active=true` with the linked
accounts belonging to that Control Panel user; signed in as a space owner; the configured
`Sync__MinimumBackgroundSyncIntervalMinutes` and `Sync__RateLimitCooldownMinutes` noted (both default
360) so cooldown expectations are exact. For FinTS additionally `FinTs__ProductId`.

Per bank:

- **Picker** — duplicate institution variants collapsed to one row; bank status shown when the feed is
  connected; a `possible problems` / `major disruption` status warns before authorization.
- **Consent** — bank flow completes, callback lands on `AUTHORIZED`, health reads `authorized` with a
  sensible `daysUntilExpiry`, accounts appear.
- **Initial history** — `strategy=longest`, no date bounds, every page followed until no
  `continuation_key` (including empty intermediate pages); dates, amounts, currency and signs match the
  bank's own statement for a sample; counterparties populated.
- **Balances** — per-account balance matches the bank; net worth reflects it in the base currency;
  multi-currency accounts are not silently converted.
- **Pending → booked** — a `PDNG` row appears and is later reconciled to the booked entry without a
  duplicate (Enable Banking only; FinTS keeps both rows).
- **Cooldown** — no background sync before the floor; `nextSyncAllowedAt` shown correctly.
- **Manual sync** — PSU headers reach the provider (all required ones or none); a normal background
  timestamp does not block it; a persisted `ASPSP_RATE_LIMIT_EXCEEDED` window does.
- **Reconnect** — before and after expiry, re-authorization keeps the same accounts and history with no
  duplicates and resumes syncing.
- **Restart safety** — `docker compose restart` mid-cycle: no duplicate transactions, cooldown and
  next-run state survive, sessions/cookies survive, no re-backfill.

After two or more banks: a member who does not own an account cannot see its transactions; every
transaction is attributed to the correct account; provider rate-limit/consent/transient errors surface
the right category and back off. On any failure capture the connection id, the sync result, the sync
history/audit entries and the banking log lines with secrets redacted.
