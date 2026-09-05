# Enable Banking AIS implementation plan

Status: static implementation complete, 2026-09-05 — no known AIS code gaps remain; real .NET CI/build and live Enable Banking validation still pending  
Scope: FullWorth self-hosted banking integration using Enable Banking AIS. Payment initiation (PIS) is explicitly out of scope.

## Official resources

Implementation must be checked against these official Enable Banking resources:

- API reference: https://enablebanking.com/docs/api/reference/
- FAQ: https://enablebanking.com/docs/faq/
- Quick Start: https://enablebanking.com/docs/api/quick-start/
- Control Panel: https://enablebanking.com/docs/api/control-panel/
- Official CLI / Control Panel API flow: https://github.com/enablebanking/enablebanking-cli
- Linked Accounts / restricted production: https://enablebanking.com/docs/api/linked-accounts/
- Terms of Service: https://enablebanking.com/terms/

Important contractual boundary for FullWorth's private testing model:

- Restricted production applications may access only accounts linked to that application.
- Personal production use is intended for the private individual who owns the linked accounts.
- A FullWorth user must not reuse another user's restricted Enable Banking application to access that user's own accounts.
- Before any public/shared hosted FullWorth banking offering, a production agreement/unrestricted application with Enable Banking is required.
- FullWorth therefore uses a bring-your-own Enable Banking model for private multi-user testing: each FullWorth user supplies their own Enable Banking application and private key.

## Definition of done

AIS is considered complete only when FullWorth correctly supports:

1. application validation,
2. ASPSP discovery and metadata,
3. authorization start,
4. authorization callback/session creation,
5. session status and lifecycle,
6. account details,
7. balances,
8. transaction list pagination,
9. transaction details,
10. correct transaction identity/deduplication,
11. first-history import with strategy=longest and no date lower bound,
12. ongoing incremental sync with strategy=default,
13. online/manual sync with PSU headers,
14. background sync without PSU headers,
15. rate-limit/cooldown handling,
16. session delete/remote consent close,
17. reauthorization without account duplication,
18. per-user Enable Banking credentials,
19. an in-app BYO Enable Banking setup wizard with automatic API registration and manual fallback,
20. optional account-scoped consent requests through access.accounts using IBAN or GenericIdentification,
21. all documented transaction_status filters (BOOK, CNCL, HOLD, OTHR, PDNG, RJCT, SCHD),
22. automated regression coverage for all above behavior.

PIS endpoints are not implemented.

## Current verification status

The repository implementation now covers the AIS definition of done above, including per-user BYO profiles,
provider/session lifecycle, account hash aliases, stable transaction reconciliation, initial/full-history
retrieval, incremental sync, PSU online context, consent close, retained-data disconnect, transaction
details, optional account-scoped consent requests, every documented transaction-status filter,
error/health handling and legacy-connection compatibility.

Static verification completed on 2026-09-05:

- Enable Banking API contracts re-audited against the current official API reference/FAQ/Terms.
- Web locale JSON parses successfully.
- `app.js` passes a JavaScript syntax parse after removing ESM import declarations.
- newly added banking test JSON literals were statically scanned and corrected.
- migration/model-snapshot deltas were wired into the repository's existing snapshot composition.
- no automatic GitHub Actions trigger was added; CI remains manual-only by repository policy.
- final lifecycle pass preserves AuthorizationStateExpiresAt across concurrent sync, keeps the old consent/status/validity intact while reauthorization is pending, consumes cancelled callback state, and marks unreachable new authorization attempts terminal instead of leaving stale PENDING rows.
- POST /sessions handling now requires the documented access.valid_until, closes malformed newly-created sessions best-effort, and never logs provider session IDs.
- transaction identity was rechecked end-to-end: entry_reference is account-scoped through the database unique key (AccountId, ExternalKey).
- the BYO wizard can now generate the RSA key server-side and register the user's Enable Banking application through the official Control Panel API flow; manual application import remains available as fallback.

This document does **not** mark the integration production-ready yet. Remaining external verification:

1. run `dotnet restore`, `dotnet build` and `dotnet test` (or manually dispatch the existing CI workflow)
   against the current main revision;
2. complete the Enable Banking sandbox checklist;
3. complete at least one restricted Production test using the tester's own linked personal account and review the real-ASPSP behavior/logs.

No further static implementation work is currently known to be required before those checks.

---

# Phase 0 - existing corrective work

Already applied:

- Remove the FullWorth 180-day initial-history limit.
- First transaction import uses strategy=longest without date_from/date_to.
- Continue pagination until continuation_key is absent, including empty intermediate pages.

---

# Phase 1 - provider client correctness

## 1.1 Authentication

Keep JWT RS256 application authentication and validate:

- header typ=JWT
- header alg=RS256
- header kid=<application id>
- iss=enablebanking.com
- aud=api.enablebanking.com
- short-lived iat/exp
- private RSA key never leaves the Banking service

JWTs must never be exposed to FullWorth.Web or a browser.

## 1.2 AIS endpoints

EnableBankingClient must expose:

- GET /application
- GET /aspsps
- POST /auth
- POST /sessions
- GET /sessions/{session_id}
- DELETE /sessions/{session_id}
- GET /accounts/{account_id}/details
- GET /accounts/{account_id}/balances
- GET /accounts/{account_id}/transactions
- GET /accounts/{account_id}/transactions/{transaction_id}

Correct the existing invalid account-details path from GET /accounts/{id} to GET /accounts/{id}/details.

## 1.3 Authorization request shape

Support the documented StartAuthorizationRequest fields:

- access
  - balances=true
  - transactions=true
  - valid_until
- aspsp { name, country }
- state
- redirect_url
- psu_type
- auth_method when selected
- credentials only when auth_method is supplied
- credentials_autosubmit when credentials are supplied
- language
- psu_id

Rules:

- psu_type is explicit and chosen from the ASPSP-supported psu_types.
- FullWorth defaults its UX to personal, but does not hard-code personal when the selected ASPSP does not support it.
- language is a two-letter lowercase UI language.
- psu_id is a stable anonymous FullWorth-generated identifier, never email, name, national ID or other direct identifier.
- credentials are transient request data and are never stored or logged.
- valid_until is capped to maximum_consent_validity.
- access.accounts is optional; when supplied FullWorth supports both documented AccountIdentification forms:
  - iban
  - other { identification, scheme_name, issuer? }
- the default UX leaves access.accounts unset so the ASPSP consent UI can choose accounts; an advanced
  connect option lets the user explicitly restrict the requested accounts when they already know the identifiers.

## 1.4 ASPSP metadata

Preserve and expose:

- name + country as the canonical ASPSP identity
- logo
- group
- beta
- bic
- psu_types
- auth_methods
- auth method approach
- auth method credentials schema
- hidden_method
- maximum_consent_validity
- required_psu_headers

Never assume a permanent Enable Banking ASPSP ID; Enable Banking identifies an ASPSP by name + country.

---

# Phase 2 - per-user Enable Banking profile

The current single global ApplicationId + PrivateKeyPath model is retained only as an optional legacy/admin fallback.

Add EnableBankingProfile:

- Id
- UserId
- ApplicationId
- EncryptedPrivateKeyPem
- KeyFingerprint
- Environment
- ApplicationName
- Active
- Services
- RedirectUrls
- VerifiedAt
- CreatedAt
- UpdatedAt

Security:

- private key encrypted with FullWorth FieldCipher/DataEncryptionKey
- never returned by a read API after creation
- logs contain fingerprint only
- profile access restricted to owner user
- BankConnection references EnableBankingProfileId
- banking provider clients are created per profile
- no cross-user client/session/key access is possible

FullWorth Spaces may contain multiple members, but a bank connection remains bound to the Enable Banking profile/user that authorized it.

---

# Phase 3 - BYO Enable Banking setup wizard

Add a settings wizard: "Enable Banking einrichten".

## Step 1 - usage notice

Explain:

- private/restricted production is for the user's own linked accounts
- shared/public/commercial usage requires an agreement with Enable Banking
- for family/friend testing each person needs their own Enable Banking account/application and linked accounts

Require acknowledgement before credentials can be saved.

## Step 2 - create Enable Banking account/application

Show the exact FullWorth callback URL:

https://<current-fullworth-host>/connect/enable-banking/callback

The wizard offers two paths.

### Automatic registration (preferred)

FullWorth follows the Control Panel flow used by Enable Banking's official CLI:

1. user enters their Enable Banking email and chooses SANDBOX or PRODUCTION;
2. FullWorth generates a 4096-bit RSA key pair inside the Banking service;
3. FullWorth requests the Enable Banking passwordless email sign-in link;
4. the email link returns to a short-lived FullWorth setup callback;
5. FullWorth exchanges the one-time code and registers the application with POST /api/applications;
6. the public key, normal FullWorth banking callback and application metadata are registered;
7. PRODUCTION registration also supplies description, GDPR email, https://fullworth.de/privacy/ and https://fullworth.de/terms/;
8. Control Panel authentication tokens are used only for this flow and are never persisted;
9. the generated private key is verified with GET /application and then stored encrypted in the user's EnableBankingProfile.

Pending automatic registrations are user-bound, single-use and short-lived. They are kept only in Banking-service memory; a service restart during the email-login window requires restarting the setup flow.

### Manual fallback

The user can instead open the official Control Panel and:

- create a Production application for private real-bank testing (or Sandbox for development);
- include AIS service;
- add the shown redirect URL;
- generate/upload a certificate or public key;
- keep the matching private key.

## Step 3 - import credentials (manual path)

Inputs:

- Application ID
- PEM private key upload

The private key is POSTed once over the authenticated FullWorth session, validated server-side, encrypted and never echoed back.

## Step 4 - verify with GET /application

Validation must check:

- JWT/key pair works
- response kid matches supplied ApplicationId
- service contains AIS
- configured callback is in redirect_urls
- environment is SANDBOX or PRODUCTION
- display environment and active state
- store application name, services, redirect URLs and verification timestamp

For a production profile, inactive state should lead to the linked-account activation instructions instead of reporting setup complete.

## Step 5 - restricted production activation

Show official "Activate by linking accounts" instructions/link.

After the user returns, "Erneut prüfen" calls GET /application and requires active=true before allowing live bank connection.

## Step 6 - finish

The profile becomes available to that FullWorth user for bank connections.

---

# Phase 4 - connection UX and authorization

Bank picker uses GET /aspsps for the selected country and selected profile.

UI shows:

- ASPSP logo/name
- beta marker
- personal/business availability
- group where helpful

After selecting a bank:

1. choose psu_type if more than one applies
2. choose a visible auth_method when multiple are available
3. render documented credential fields when that auth method accepts credentials
4. validate credential patterns client-side for UX and server-side for trust
5. do not persist credentials
6. start POST /auth
7. redirect the browser to the provider's returned URL in the normal system browser/navigation context

The callback state remains cryptographically random, short-lived, bound to user + FullWorth Space + EnableBankingProfile and single-use.

---

# Phase 5 - session and consent lifecycle

Persist documented session states:

- AUTHORIZED
- CANCELLED
- CLOSED
- EXPIRED
- INVALID
- PENDING_AUTHORIZATION
- RETURNED_FROM_BANK
- REVOKED

Unknown provider states are preserved safely rather than crashing.

After POST /sessions:

- persist session_id encrypted
- persist effective access.valid_until
- immediately start initial sync
- user-visible authorization success must not be reversed merely because initial data retrieval temporarily fails

Reauthorization:

- refetch current ASPSP list before reconnect to handle bank rebrands/name changes
- reuse the existing BankConnection
- match accounts by identification_hash/identification_hashes
- never use session-scoped uid as durable identity
- replace session id atomically
- trigger initial/refresh sync after new authorization

Disconnect:

1. call DELETE /sessions/{session_id}
2. tolerate already missing/closed sessions
3. mark connection CLOSED locally
4. execute the user's chosen local data deletion policy

DELETE /sessions is expected to close the PSU bank consent where possible.

---

# Phase 6 - account identity and metadata

Durable account identity:

- identification_hash is primary cross-session identity
- identification_hashes may be stored for fuzzy matching/reconciliation
- uid is session-scoped and never the durable account key

Account details endpoint:

GET /accounts/{uid}/details

Persist useful normalized metadata without exposing unnecessary personal data:

- display/details
- product
- cash_account_type
- currency
- account identifier last4/masked representation
- usage
- optional credit limit when useful
- identification hashes

Do not persist AccountResource.name (account-holder name), postal addresses, legal-age data or other account-holder identity fields unless FullWorth later has a concrete product requirement. Display names use account details/product metadata instead.

If /details is unsupported (404) but the session provides sufficient account metadata, sync continues.

If a session only returns uid and no stable hash, resolve /details before ingesting. If no identification_hash can be resolved, skip the account rather than creating a duplicate-prone account.

---

# Phase 7 - transaction identity and ingest

Correct identity rules:

1. BOOK transaction with non-empty entry_reference:
   external key = stable account identification_hash + entry_reference
2. transaction_id is never a durable deduplication key
3. transaction_id is stored only as a detail-fetch pointer
4. when entry_reference is unavailable:
   - use a deterministic FullWorth fingerprint based on stable normalized fields
   - fingerprint must be account-scoped
5. pending transactions without a stable entry_reference are not matched to booked transactions solely by transaction_id

Ingest must update an existing matched transaction rather than duplicate it.

Store both:

- EntryReference
- ProviderTransactionId

Raw provider JSON remains encrypted at rest under the existing policy.

---

# Phase 8 - first import and ongoing synchronization

## Initial sync

Run immediately after successful authorization.

For each account:

- details
- balances
- GET transactions with strategy=longest
- transaction_status is supported as an optional provider query filter for all documented status values
- omit date_from
- omit date_to
- continue until continuation_key is absent
- continue even when transactions=[] if continuation_key exists

The API may expose different history lengths per ASPSP. FullWorth must not impose an artificial historical lower bound.

MaxPagesPerAccount remains only a safety fuse. If reached, record an explicit incomplete-sync error and do not present the account as fully synced.

## Incremental/background sync

Use strategy=default.

For each account:

- determine latest successfully stored BOOK booking date
- date_from = latest date - overlap window
- date_to = today
- fetch all continuation pages
- upsert changed/booked transactions
- refresh balances

Default overlap stays 7 days unless real-bank testing proves a better value.

Handle WRONG_TRANSACTIONS_PERIOD explicitly: narrow/recover safely rather than blindly retrying the same invalid period.

---

# Phase 9 - online vs background retrieval and PSU headers

Enable Banking states that PSU headers indicate that the user is actively online.

## Background

Scheduled/worker fetch:

- send no PSU headers
- enforce at least six hours between background attempts per connection
- on ASPSP_RATE_LIMIT_EXCEEDED wait at least six hours and honor a longer Retry-After
- never retry 429 immediately

## User-triggered online fetch

Manual "Jetzt synchronisieren", user-requested older range and transaction-detail loading:

- FullWorth.Web captures available request values
- forward a structured PsuContext internally, not arbitrary user headers
- Banking service sends documented PSU headers

Supported values:

- Psu-Ip-Address
- Psu-User-Agent
- Psu-Referer
- Psu-Accept
- Psu-Accept-Charset
- Psu-Accept-Encoding
- Psu-Accept-language
- Psu-Geo-Location only with explicit user permission

Per ASPSP required_psu_headers:

- either all required PSU headers must be available and sent
- or send none and treat the request as background
- never send a partial required set, because Enable Banking returns PSU_HEADER_NOT_PROVIDED

PSU context must not be persisted in bank connection rows or logs.

---

# Phase 10 - transaction details

Add an authenticated FullWorth endpoint to load additional details for a stored transaction only when ProviderTransactionId is available.

Flow:

1. authorize current FullWorth user/space
2. resolve connection/account/uid
3. use that connection's EnableBankingProfile
4. fetch GET /accounts/{uid}/transactions/{transaction_id}
5. include online PSU context when available
6. return normalized/safe details
7. optionally cache normalized detail fields; do not depend on transaction_id for identity

Do not bulk-fetch transaction details during every background sync.

---

# Phase 11 - scheduling, error model and health

Worker may wake frequently to inspect due connections, but actual background provider access must respect per-connection six-hour cadence.

Persist safe error categories, including:

- ASPSP_RATE_LIMIT_EXCEEDED
- PSU_HEADER_NOT_PROVIDED
- WRONG_TRANSACTIONS_PERIOD
- SESSION_EXPIRED
- SESSION_REVOKED
- SESSION_CLOSED
- AUTHORIZATION_FAILED
- ENABLE_BANKING_AUTH_FAILED
- ACCOUNT_RESOLUTION_FAILED
- HISTORY_PAGE_LIMIT_REACHED
- PROVIDER_UNAVAILABLE
- SYNC_FAILED

Never store raw provider error bodies as user-visible error messages. HTTP 401/403 alone is not treated as an expired session: only explicit session/consent error codes trigger reauthorization; otherwise FullWorth records ENABLE_BANKING_AUTH_FAILED so the user's application/key can be rechecked.

Health UI:

- healthy
- syncing
- retry scheduled
- reauthorization required
- expired
- revoked/closed
- partial history/error

Notify owners on health transitions, not on every failed polling iteration.

---

# Phase 12 - migration/backward compatibility

Existing global-config installations must continue to boot.

Migration rules:

- existing BankConnection rows may initially have EnableBankingProfileId=null
- global ApplicationId/private-key config forms a legacy provider context for these rows
- new user-created connections require an EnableBankingProfile
- no plaintext key migration into the database is automatic
- legacy administrators migrate explicitly by re-entering the legacy Application ID + matching PEM in their own BYO settings wizard; FullWorth never copies a global secret into an arbitrary user's profile automatically

No destructive migration of existing transactions.

The change from transaction_id-first to entry_reference-first identity requires reconciliation:

- existing rows retain their DB IDs
- on re-ingest, prefer matching same account + entry_reference
- if a historical row was keyed by transaction_id, update its external identity when a unique entry_reference match is proven
- do not automatically merge ambiguous rows

---

# Phase 13 - automated tests

Provider contract tests:

- correct JWT claims/header
- GET /accounts/{id}/details path
- DELETE session
- transaction-detail path
- StartAuthorizationRequest optional fields
- credentials require auth_method
- psu_type and language
- maximum consent validity cap
- access.accounts serializes IBAN and GenericIdentification exactly as documented
- transaction_status accepts every documented status and rejects unknown values before provider access

History tests:

- first import has strategy=longest and no dates
- empty page + continuation key continues
- every continuation page preserves correct request mode
- page safety fuse records incomplete sync

Identity tests:

- entry_reference preferred over transaction_id
- same entry_reference across new session does not duplicate
- changed transaction_id does not duplicate
- missing entry_reference uses fingerprint
- pending transaction without stable entry_reference is not incorrectly merged

PSU tests:

- background contains no PSU headers
- online sends available headers
- required set complete -> online
- required set incomplete -> send none
- PSU context not logged/persisted

Rate-limit tests:

- 429 never immediate retry
- Retry-After honored
- six-hour fallback
- persisted cooldown survives restart

Profile/tenancy tests:

- profile key encrypted
- read API never returns private key
- user A cannot read/use user B profile
- connection must reference caller-owned profile
- callback state bound to profile/user/space

Wizard verification tests:

- automatic registration requests passwordless Control Panel login without persisting tokens
- automatic registration sends the generated public key, configured callback and required Production legal/contact fields
- automatic callback state is random, user-bound, single-use and expires
- automatic registration verifies the returned app before persisting the generated private key
- automatic failure leaves the manual setup path available
- valid app succeeds
- kid mismatch rejected
- missing AIS rejected
- redirect URL mismatch rejected
- inactive production app shown as linked-account activation required
- active production app completes setup

Session lifecycle tests:

- expired/revoked/closed statuses request reauthorization
- DELETE session called on disconnect
- remote 404 on delete is safe/idempotent
- reconnect reuses connection/account identities

Real-bank test checklist remains in docs/LIVE_BANK_TEST_PLAN.md and must include at least one restricted production account before the integration is marked production-ready.

---

# Phase 14 - rollout order

Implementation order:

1. provider endpoint fixes + identity fix
2. PSU request context + sync semantics
3. consent/session delete and health states
4. EnableBankingProfile database + encryption + tenancy
5. per-profile client factory
6. wizard/backend profile APIs
7. frontend wizard + bank picker/auth metadata
8. transaction-details endpoint/UI
9. migration reconciliation
10. full automated suite
11. sandbox live test
12. restricted production personal account test
13. documentation audit against current Enable Banking API reference

No part should expose the Enable Banking private key, JWT or provider session ID to the browser.
