# Security architecture

What FullWorth actually enforces, and where in the source it is enforced. Verification commands and
the tests that lock these invariants are in [Testing](TESTING.md).

## Process and network boundary

The module composition and the loopback hops are described in
[Architecture](ARCHITECTURE.md). What matters for security:

- `docker-compose.yml` publishes only
  `${FULLWORTH_BIND_ADDRESS:-127.0.0.1}:${FULLWORTH_PORT:-8098}:8080`. PostgreSQL and the optional
  `fullworth-codex` bridge have no published port at all. Both application containers run
  `read_only: true` with `no-new-privileges:true` and a tmpfs `/tmp`.
- Running Web, Backend and Banking in one process does **not** merge the trust boundaries. Their
  middleware pipelines are branched in by path and still demand their own keys and user context, and
  the Web module still reaches them as HTTP clients — over loopback, through the same guards the
  split topology used.
- The browser never receives a database, backend or banking credential, and it can only reach
  same-origin Web routes.

## Identity and credentials

Auth identity lives in the `auth` schema of the same PostgreSQL database, on ASP.NET Core Identity
(`AuthDbContext`, `AuthUser`). Configured in `AuthOptions` / `appsettings.json`:

- minimum password length **12**, no character-class requirements, `RequireUniqueEmail = true`;
- lockout after **5** failed attempts for **15 minutes**, enabled for new users;
- `SignIn.RequireConfirmedAccount` and `RequireConfirmedEmail` are both `false` — FullWorth never
  verifies an e-mail address.

### Login

`POST /auth/login` (`AuthSessionCoordinator.LoginAsync`) validates the password, refuses disabled and
locked-out users, and — when `TwoFactorEnabled` — returns `two_factor_required` and only issues a
session after a valid authenticator code. A wrong TOTP code counts as an access failure. The pending
e-mail/password pair exists only in the login page's JavaScript memory between the two POSTs; it is
never written to `localStorage`, `sessionStorage` or IndexedDB.

### TOTP two-factor

Ordinary authenticator apps via ASP.NET Identity's authenticator provider:
`GET /auth/two-factor/status`, `POST /auth/two-factor/setup|enable|disable` (`TwoFactorService`).
Enabling requires a valid code; disabling requires a valid code and resets the authenticator key.

### Passkeys

FIDO2 through `Fido2NetLib` (`src/FullWorth.Web/Modules/Passkeys/`). `PasskeyOptions.Validate`
rejects a missing relying-party id/name, an empty origin list, origins carrying a path/query/fragment,
a challenge lifetime above 10 minutes, and — in Production — any non-HTTPS origin. Compose derives
`Passkeys__RelyingPartyId` and `Passkeys__Origins__0` from `FULLWORTH_DOMAIN`. Challenges are
persisted and expired (`PasskeyChallengeCleanup`); passkey login goes through the same
`SessionService.CreateSessionAsync` path as password login.

### Recovery codes

`RecoveryService` issues 10 single-use codes by default (`Recovery:CodeCount`, hard maximum 50),
stores only their SHA-256 hashes, and consumes a code on redemption.
`POST /auth/recovery-code/redeem` is anonymous and rate-limited under the `PasswordReset` policy.

**This is the only working self-service recovery path.** `POST /auth/password-reset/request` generates
an Identity reset token and *discards* it (`_ = await auth.GeneratePasswordResetTokenAsync(...)`) —
there is no mail transport, so the always-`202` response is truthful but nothing is delivered.
`POST /auth/password-reset/complete` works if a token is obtained out of band, and revokes all
sessions on success.

### External sign-in

Google and Apple are registered only when their credentials are configured; `GET /auth/providers`
reports what exists. Sign-in is rate-limited under the `Login` policy and still passes through TOTP
when enabled (`/auth/external/two-factor`). Registration through a provider obeys the same
registration gate as `POST /auth/register`.

Linking caveat, as implemented in `AuthEndpoints.ExternalCallbackAsync`: when no
`AspNetUserLogins` row matches, FullWorth looks up an existing local account by the provider's
`email` claim and calls `AddLoginAsync`. It does **not** inspect an `email_verified` claim. The
security of that link rests entirely on Google/Apple not asserting an unverified address.

### Registration gate

`RegistrationService` always allows the *first* account on an empty instance (serialised through
`FirstRegistrationGate`), marks it `IsAdmin`, and then requires `Registration:Enabled` for every
further sign-up. Compose defaults `Registration__Enabled` to `false`.

### PIN app-lock

`ui/lock.js` blanks the UI after 10 minutes without `pointerdown`/`keydown`/`touchstart` and unlocks
with a passkey, falling back to a PIN. The server session stays valid throughout — this gates the UI
only, and re-locks on reload.

`PinService` treats the PIN as a secondary factor, never a primary credential: 4–12 ASCII digits,
hashed with the same `IPasswordHasher<AuthUser>` (PBKDF2) as passwords, stored as an Identity
authentication token in `AspNetUserTokens` (provider `Finance.Lock`), 5 wrong entries → 5-minute
lockout. `/auth/pin` requires an authenticated session and `/auth/pin/verify` is rate-limited under
the `Login` policy.

## Sessions

Server-side, revocable sessions (`src/FullWorth.Web/Modules/Sessions/`, contract in
[INTEGRATION.md](../src/FullWorth.Web/Modules/Sessions/INTEGRATION.md)). Deployed defaults from
`appsettings.json`: idle timeout **7 days**, absolute lifetime **7 days**, touch interval 1 hour,
expired/revoked rows retained 30 days.

`SessionCookiePolicy`: `HttpOnly`, `SameSite=Lax`, `Path=/`, essential, sliding expiration. In
Production the cookie is named `__Host-Finance.Auth` with `SecurePolicy = Always`; in development it
is `Finance.Auth` with `SameAsRequest` so local HTTP still works. No login request accepts a session
id — `CreateSessionAsync` generates it server-side, which is what prevents session fixation.

Every request re-validates the cookie in `ValidateFinancePrincipalAsync`: the principal must carry a
parseable user id and `session_id`, the `AuthUser` must exist and not be `IsDisabled`, and
`SessionService.ValidateSessionAsync` must accept the session against the current Identity security
stamp. Deliberately *not* checked here: `IsLockedOutAsync` — a transient password lockout must not
terminate an established session, which would turn brute-force protection into a session DoS. An IP
change alone never invalidates a session.

Session-invalidating events:

- change password → `RevokeAllSessionsAsync` + Identity sign-out;
- complete password reset → `RevokeAllSessionsAsync`;
- admin revoke-sessions → `RevokeForSecurityEventAsync`;
- account-deletion request → all *other* sessions revoked, current one kept for the recovery screen.

## Tenant isolation

Users belong to one or more FullWorth Spaces (`FullWorthSpaceMembers`); accounts additionally carry
per-user `AccountOwner` grants with `owner` or `viewer` ownership. IDs and UUIDs identify resources
and never grant access.

The acting user id is the only identity the backend trusts, and it arrives exclusively in the
`X-FullWorth-User-Id` header that the Web module sets from the authenticated session. The **space id
is caller-supplied** — it travels as a normal route/query parameter — so it is a *request* for a
scope, not a grant. Every space-scoped operation re-verifies membership against the header-derived
user (`FullWorthSpaceStore.IsMemberAsync`, `AccountService.CanUserAccessAsync` /
`CanUserEditAsync`) before touching data. An inaccessible resource must answer 404, not a distinct
"exists but forbidden" response.

## BFF and internal-key seams

Public browser traffic uses same-origin BFF routes only. `Program.cs` maps exactly three proxies with
explicit path allowlists:

| Route | Allowlist | Rate limit |
| --- | --- | --- |
| `/bff/backend/{**path}` | `/api/` | `BrowserApi` |
| `/bff/backend/api/purchases/receipt-imports/upload` | `/api/` | `ReceiptUpload` |
| `/bff/banking/{**path}` | `/api/banking/` | `BrowserApi` |

All three `RequireAuthorization()`. `/bff/backend/api/bootstrap*` is explicitly answered **404**: the
bootstrap seam runs with no user context, so an authenticated browser session must never be able to
borrow the internal key for it. The backend's `/internal/*` ingest surface is unreachable through the
BFF because it is not under an allowlisted prefix.

Two independent gates protect every outbound internal call, and the keys are attached only after the
second one:

1. **`ProxyTargetValidator.TryBuildTarget`** (route level) composes the attacker-controlled path
   against the configured `BaseAddress` and accepts it only when scheme, host and port match exactly,
   there is no userinfo or fragment, the normalised absolute path starts with an allowlisted prefix,
   and the path contains no `\`, `%5c`, `%2f` or `%25`. Dot segments are normalised during
   composition, so `api/../admin` fails the prefix check. A rejected request produces zero outbound
   traffic.
2. **The outbound handlers** (`BackendUserContextHandler`, `BankingUserContextHandler`,
   `ServiceProxyGuardHandler`) re-resolve the final absolute URI and repeat the origin check
   (`ProxyTargetValidator.IsSameOrigin`) before adding anything. No trusted header value is ever
   forwarded from the inbound request: every header in
   `BackendContextHeaders.UntrustedForwardingHeaders` (all internal/user/space/legacy/ingest keys plus
   `Authorization` and `Cookie`) and every `Psu-*` value is stripped first and then rebuilt from the
   real ASP.NET request. `ARCHITECTURE.md` has the hop-by-hop detail.

The keys are therefore attached in exactly one place each, after two independent origin checks. That
loopback check matters more, not less, in unified mode: the target is `http://127.0.0.1:8080`, this
very process, so a missing check would turn any path-traversal bug into an internal-key leak to
whatever else the host can reach.

Receiving side:

- **Backend `/api/*`** — `InternalUserContextMiddleware` requires exactly one
  `X-FullWorth-Internal-Key` matching `Security:InternalKey` in fixed time, then exactly one parseable
  `X-FullWorth-User-Id` resolving to an **active** `FullWorthUser`; otherwise 401. `/api/bootstrap/*`
  is the single exception that runs on a valid key alone (first-run admin, invite accept, and the
  deactivate/reactivate/purge-user calls).
- **Backend `/internal/*`** — server-to-server ingest from the Banking module, gated on
  `X-FullWorth-Ingest-Key` vs `Security:IngestKey`.
- **Banking `/api/*`** — gated on `X-FullWorth-Banking-Key` vs `Security:ApiKey`; the Enable Banking
  callbacks under `/connect/enable-banking/*` are intentionally public.

`BackendContextOptions.Load` refuses to start when `Services:BackendInternalKey` is shorter than 32
characters and, in Production, when it looks like a placeholder (`default`, `change-me`,
`placeholder`, `generate-*`, `replace-*`).

## Antiforgery and rate limits

`FullWorthAntiforgeryValidationMiddleware` validates POST/PUT/PATCH/DELETE for any routed request
under `/auth` or `/bff` and answers 400 `{"error":"Invalid antiforgery token."}` on failure. Header
`X-CSRF-TOKEN`, cookie `Finance.Antiforgery`, token issued by `GET /auth/antiforgery`.

Rate-limit defaults (`RateLimitOptions`, overridable under `RateLimits:`; a non-positive limit or
window fails validation):

| Policy | Permits / window | Applied to |
| --- | --- | --- |
| `Login` | 10 / 5 min | `/auth/login`, external sign-in, `/auth/pin/verify` |
| `Registration` | 5 / 60 min | `/auth/register` |
| `PasswordReset` | 5 / 15 min | password reset, `/auth/claim`, recovery-code redeem |
| `Passkey` | 20 / 5 min | passkey endpoints |
| `BrowserApi` | 600 / 60 s | BFF proxies, account-deletion request/cancel |
| `ReceiptUpload` | 10 / 10 min | bulk receipt upload |

`Registration` is deliberately a separate bucket from `Login`: on an instance with open registration
an anonymous POST creates persistent state, and sharing the bucket meant a sign-up flood locked real
logins out of the same NAT.

## Response headers and CSP

Emitted by `SecurityHeadersMiddleware` on `Response.OnStarting`, so success **and** error responses
carry them. Exact values from `SecurityHeadersPolicy`:

```text
Content-Security-Policy: default-src 'self'; base-uri 'self'; object-src 'none';
  frame-ancestors 'none'; form-action 'self'; script-src 'self'; style-src 'self';
  style-src-attr 'unsafe-inline';
  img-src 'self' data: https://enablebanking.com https://*.enablebanking.com;
  font-src 'self'; connect-src 'self'; frame-src 'none'; worker-src 'self';
  manifest-src 'self'; media-src 'self';
X-Content-Type-Options: nosniff
Referrer-Policy: strict-origin-when-cross-origin
Permissions-Policy: camera=(), microphone=(), geolocation=(), payment=(), usb=(), serial=(),
  accelerometer=(), gyroscope=(), magnetometer=()
X-Frame-Options: DENY
```

The policy is a constant: no directive is ever derived from a request header or `Origin`.
`SecurityHeadersOptions.ReportOnly` swaps to `Content-Security-Policy-Report-Only` and removes the
enforcing header (and vice versa) so the two can never both be present.
`AddLegacyFrameProtection` (default `true`) controls `X-Frame-Options` only.

Consequences the frontend must live with: **no inline `<script>`, no `<style>` block, no `on*`
attribute, no `javascript:` URL, no `eval`/`new Function`, and no static `style="…"` attribute.**
`style-src-attr 'unsafe-inline'` exists only so a `${…}` template binding can push a dynamic value
into a CSS custom property or computed dimension.

`img-src` allows `enablebanking.com` because institution logos are loaded from the provider.

## Transport, hosts and proxy trust

HSTS is Production-only, `max-age` 180 days, `IncludeSubDomains = false`, `Preload = false`
(`SecurityHeadersPolicy.HstsMaxAge`, `ShouldUseHsts`). Deliberately conservative: a self-hoster's
apex domain usually serves other things.

HTTPS redirection is Production-only. In unified mode it is wrapped in
`app.UseWhen(remote address is not loopback)`, because the internal module calls stay on loopback HTTP
inside the process and would otherwise be redirected to themselves.

Production start-up throws if `AllowedHosts` is unset or contains `*`. Compose sets it to
`${FULLWORTH_DOMAIN};127.0.0.1;localhost`.

`ForwardedHeadersOptions` accepts `X-Forwarded-For`/`X-Forwarded-Proto` with `ForwardLimit = 1` and
only from `ReverseProxy:KnownProxies` / `ReverseProxy:KnownNetworks` (compose defaults the network to
`172.16.0.0/12`, the Docker bridge range, because bridge gateway addresses are ephemeral).

`GET /appsettings.json` is mapped to 404 so a misconfigured static-file root cannot serve
configuration.

## Uploads, documents and browser automation

Receipt and document uploads validate the extension against
`.jpg/.jpeg/.png/.webp/.heic/.pdf`, then check that the **leading bytes actually match** the claimed
type (`ReceiptSignature.Matches`), so an HTML/script/executable renamed to `.jpg` cannot be stored and
later served. Stored names are server-generated; downloads are authorised and answered with
`X-Content-Type-Options: nosniff` and a mapped content type
(`PurchaseDocumentService`, `PurchaseCaptureEndpoints`). Bulk upload bodies are capped by
`ReceiptImports:MaxUploadBytes` (default 512 MiB, clamped to 1 GiB) and only on that one endpoint.

The Amazon connector's sanctioned acquisition path is server-side browser automation
(`Modules/Purchases/Amazon/AmazonBrowserAutomation.cs`; rationale in
[Product decisions](PRODUCT_DECISIONS.md)). The user's Amazon e-mail/password and any OTP are
submitted only to Amazon's own sign-in form inside the container's Playwright/Chromium runtime, are
never persisted and never returned to the browser. Only the resulting encrypted session state is
stored, and it is reused for the automatic syncs. Bank credentials follow the same rule: they enter
only the guarded Banking module path, are never returned to the browser, and are persisted encrypted
through the backend.

The unified image is built on `mcr.microsoft.com/playwright/dotnet` and adds `tesseract-ocr`,
`tesseract-ocr-deu` and `poppler-utils`, so Amazon, receipt-OCR and PDF features keep the same
isolation and validation behaviour that the split backend image had.

Banking keeps its own conservative synchronisation, retry and provider-rate policies — see
[Banking safety](BANKING_SAFETY.md).

## Service worker and PWA

`sw.js` caches the static application shell only. `isSensitive(url)` excludes `/api`, `/bff`,
`/auth`, `/share` and `/connect`, so no finance response, receipt or auth response is ever written to
the offline cache. Through the BFF, cache validators (`ETag`, `Cache-Control`, `Last-Modified`) are
forwarded for exactly one path prefix, `/api/intelligence/brand-assets/` — immutable non-financial
bytes addressed by SHA-256 — and dropped for everything else, so a backend cache header can never make
a finance response browser-cacheable.

## Secrets and encryption at rest

One `FULLWORTH_SECRET` drives a normal install. `docker-compose.yml` fans it out into the Postgres
password, `Security:InternalKey`, `Security:IngestKey`, `Security:ApiKey`, `Security:MasterKey` and
the matching `Services:*` values, each with a per-key override (`FULLWORTH_BACKEND_INTERNAL_KEY`,
`FULLWORTH_INGEST_KEY`, `FULLWORTH_BANKING_API_KEY`, `FULLWORTH_DATA_ENCRYPTION_KEY`) so existing
installations can keep separate credentials.

`SecretBootstrap` (evaluated before any configuration read):

- **`_FILE` convention** — every `<KEY>_FILE` environment variable pointing at a readable file injects
  that file's trimmed contents as configuration key `<KEY>` (with `__` → `:`), so Docker secrets work
  without putting values in the environment. The file wins over a plain environment value.
- **Fail closed in Production** — `RequireSecret` throws when a required secret is missing or looks
  like a placeholder. Enforced for `ConnectionStrings:AuthDatabase` and `Services:BankingApiKey`
  (Web), `ConnectionStrings:FullWorth` and `Security:IngestKey` (Backend), `Security:ApiKey` and
  `Backend:IngestKey` (Banking). `Services:BackendInternalKey` is validated separately and more
  strictly by `BackendContextOptions.Load`. For connection strings the check also rejects the exact
  committed development passwords
  (`password=finance|fullworth|postgres`); for keys it rejects anything under 16 characters. The
  offending key *name* is reported; the value never is. Outside Production this is a no-op so dev and
  tests run with blank defaults.

`FieldCipher` encrypts individual sensitive columns with **AES-256-GCM**, a fresh random nonce per
value (so ciphertext is non-deterministic), stored as `v1:` + base64(nonce‖tag‖ciphertext). Values
that must still be unique or looked up by value get a keyed HMAC-SHA256 **blind index** whose key is
HKDF-derived from the same master key with info `fullworth-blind-index`. The key comes from
`Security:DataEncryptionKey` (base64, exactly 32 bytes) or is derived as
`SHA256("fullworth:data-encryption:v1:" + Security:MasterKey)`; a master key under 32 characters or
holding a placeholder is rejected. In Production one of the two is mandatory. Outside Production a
missing key yields an identity cipher, which is why dev/test databases hold plaintext.

Because the field-encryption key is derived from `FULLWORTH_SECRET`, that secret is intentionally
**stable**: changing it while encrypted data exists requires a controlled re-encryption migration. See
[Operations](OPERATIONS.md).

The Data Protection key ring (auth cookies, antiforgery tokens) is persisted to
`DataProtection:KeyPath` under application name `FullWorth.Web`; compose mounts a volume at
`/data/dataprotection`. Without it the ring is ephemeral and every restart invalidates sessions and
produces spurious CSRF failures — so that directory belongs in the backup set.

Backups contain everything sensitive: narrow-scope credentials, and restore verification
(`ops/restore-test/verify-restore.sh`) as the proof.

## Instance administration

`AuthUser.IsAdmin` is the single instance-admin flag. `InstanceAdminBootstrapper.EnsureAsync` runs at
start-up and is idempotent: it does nothing if an admin exists or if there are no users at all,
otherwise it prefers the account matching `Bootstrap:Email` and falls back to the oldest account.
Public registration and invite-created accounts are never promoted automatically — except the very
first account on an empty instance, which `RegistrationService` marks admin.

The UI is not the boundary. `/admin` and every `/auth/admin/*` endpoint independently resolve the
caller through `InstanceAdminService.GetCurrentAdminAsync`, which requires `IsAdmin`, not
`IsDisabled`, and no pending deletion; a normal user gets **403**. A separate middleware also denies
`/admin/*` static assets to non-admins, so the admin shell's own JS/CSS is not readable either. The
normal app learns only what `GET /auth/capabilities` returns: `{ admin, twoFactorEnabled }`.

The admin surface is deliberately login/account administration only. It exposes auth user id, e-mail,
created/updated timestamps, disabled state, admin state, TOTP state, session device names and
last-seen timestamps, the account-deletion lifecycle state and the bounded technical purge error code
(`AdminUserListItemDto`, `AdminUserDetailDto`). It exposes **no** spaces, accounts, IBANs, balances,
transactions, categories, merchants, purchases, receipts, contracts, assets or AI conversations.
Overview counts are auth-only: users, active, disabled, pending deletion, failed deletion, admins.

Actions: disable, enable, revoke all sessions, grant admin, revoke admin, schedule the standard 7-day
deletion, cancel deletion before the lease is taken. Admin-triggered deletion reuses the same
`AccountDeletionService` as self-service. There is no force-purge button. Three of these refuse with
`last_admin` when the target is the only operational admin (`HasOtherOperationalAdminAsync`), so an
instance cannot be locked out of its own administration.

Mutations are appended to the auth database as `AdminAuditEvent` — actor, optional target, action,
outcome, timestamp. No financial payload, password, TOTP key, token or request body.

## Account deletion and purge

A recoverable, fail-closed flow: `AccountDeletionService`, `AccountDeletionPurgeWorker` (Web, because
the schedule lives in the auth database) and `AccountPurgeService` + `PersonalDataPurgeManifest`
(Backend).

**Request.** `POST /auth/account-deletion/request` needs an authenticated session, re-authentication
with the current password (`CheckPasswordAsync`) and an explicit destructive confirmation in the
dialog. It records `DeletionRequestedAt` and `DeletionScheduledFor = now + AccountDeletion:RecoveryWindow`
(default 7 days; the option validator refuses anything under one day, so a zero-day destructive
delete cannot be configured), revokes all *other* sessions, and calls the internal
`api/bootstrap/deactivate-user`. The finance user's `IsActive` flips to `false`, which
`InternalUserContextMiddleware` already rejects, and scheduled bank sync skips connections whose
authorising user is inactive or tombstoned — without revoking the Enable Banking provider session, so
reactivation stays cheap.

**Blocked state.** `PendingDeletionAccessMiddleware` allows only `/account/deletion`,
`/account-deletion`, `/auth/account-deletion`, `/auth/antiforgery`, `/auth/logout`, `/health`, `/pwa`
and `/favicon.ico`. Any other `/bff` or `/api` call returns **423 Locked** with
`{"error":"account_pending_deletion", deletionScheduledFor}`; any other page redirects to
`/account/deletion`. A user who logs in during the window lands there, never in the finance UI.

**Reactivation.** `POST /auth/account-deletion/cancel` calls the backend reactivation first and clears
`DeletionRequestedAt`/`DeletionScheduledFor`/`DeletionLeaseUntil`/`DeletionLastError` only after that
succeeds. It refuses once a purge lease is held.

**Purge.** The worker wakes on `AccountDeletion:PurgeInterval` (default 1 h, minimum 5 min), takes at
most 20 due users, and for each one:

1. acquires a lease with a single conditional `ExecuteUpdateAsync` setting
   `DeletionLeaseUntil = now + PurgeLease` (default 15 min, minimum 1 min) — if that updates 0 rows
   another worker owns it and this one returns;
2. `POST api/bootstrap/purge-user` with `financeUserId` and the internal key (never a value from the
   browser);
3. only on 200/204 deletes the `AuthUser`, letting Identity cascades take sessions, recovery codes,
   passkeys, PIN token and claims with it.

Anything else releases the lease and records a bounded error code (`backend_<status>`,
`purge_exception`, `auth_delete_failed`) with no payload. The ordering is the point: a backend failure
must never leave the login identity deleted while finance data survives with nobody able to recover or
inspect it.

**Finance-side rules** (`AccountPurgeService`, staged per space with idempotent steps rather than one
giant transaction): a space where the deleting user is the only member is deleted with all its data,
including stored purchase files. A shared space is preserved — only the departing user's
`AccountOwner` rows, membership, preferences, push devices, Enable Banking profiles and
attributable Intelligence outbox items are removed, and their banking authorisation is closed
without destroying data other members still own.

**Tombstone.** `UserStore.TombstoneAsync` rewrites the surviving `FullWorthUser` row to
`EmailNormalized = "DELETED-<GUID-N>@INVALID.FULLWORTH"`, `DisplayName = "Deleted user"`,
`IsActive = false`, `IsTombstone = true`, and clears onboarding fields. This is a referential-integrity
placeholder, not an analytics identity: each deleted user keeps a *distinct* GUID, and `IsTombstone`
is the flag that user-level analytics must filter on — never the display name, which would collapse
several deleted people into one bucket.

**Purge manifest.** `PersonalDataPurgeManifest.Describe` walks the EF model and classifies every mapped
entity as space-owned (with ownership depth), user-owned, historical-user-referencing, explicitly
global, the user identity, or the space root. A test fails when anything is unclassified, which forces
a new table to get an explicit deletion decision before CI can pass. Today the guard covers
`FullWorthDbContext` only; extending it to `IntelligenceDbContext` and `AuthDbContext` is tracked in
[Open items](OPEN_ITEMS.md).

The 7-day window is a state transition, not a backup restore. After purge, deleted rows may still
exist in ordinary database backups until those expire; backups are never selectively edited, and a
restore must not be used to resurrect an account past the deadline outside controlled disaster
recovery.

## Intelligence Cloud

[Cloud](CLOUD.md) documents the instance side: the consent gate, the optional enrollment token
(currently empty in Compose, so enrollment relies on the Cloud's public registration), what the
observation outbox is allowed to contain, and knowledge-pack `RSA-PSS-SHA256` verification.

The verification key is **pinned, not shipped**. An instance with none fetches it from the Cloud it
is enrolled with and stores it per Cloud origin, create-only; configuration still overrules the pin.
The security property is the pin, not the fetch: after the first sync a different key is recorded and
refused (`knowledge_pack_public_key_changed`) and only an audited admin action can replace it, so a
later endpoint compromise cannot make this instance accept another publisher's packs. The first fetch
itself is trusted on TLS to a non-configurable endpoint — a smaller window than the previous state,
in which the shipped `OfficialPublicKeyPem` was empty and no external instance could verify anything
at all.

Two rules bind Cloud to the rest of this document: outbound payloads may carry only anonymous
aggregates plus the instance id — never a finance user id or e-mail — and account deletion removes
unsent, user-attributable outbox items locally (`AccountPurgeService.PurgeIntelligenceUserDataAsync`).
Already-accepted anonymous aggregates may remain; there is currently no remote retraction call, which
is why the payload-anonymity rule has to hold at submission time.

## Release gate

Before exposing a deployment publicly, verify authentication and recovery, resource authorization,
CSRF and rate-limit behaviour, the internal-key boundaries, response-data boundaries, security
headers, upload handling, dependency review and a successful restore test. The commands and the tests
that already cover each of these are in [Testing](TESTING.md); operator procedure is in
[Operations](OPERATIONS.md).
