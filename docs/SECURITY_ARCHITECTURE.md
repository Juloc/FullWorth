# Security architecture

## Boundaries

The canonical deployment exposes one application process:

```text
Browser
  |
  v
FullWorth
  |- Web / authentication / BFF
  |- Finance backend module
  |- Banking module ---------> Enable Banking / FinTS
  |
  v
PostgreSQL
```

Only the `fullworth` container is reachable through the reverse proxy. PostgreSQL remains private.
The optional Codex bridge stays private on the Compose network.

Web, Backend and Banking remain separate code modules and retain their existing authorization,
internal-key and user-context boundaries even though they run in one ASP.NET process. Internal module
calls use loopback and are never exposed as trusted browser calls.

The browser never receives database, backend or banking credentials. The Web module derives the
authenticated user and FullWorth Space server-side before guarded internal calls are made.

## Identity and authorization

Users belong to one or more FullWorth Spaces. Accounts can additionally grant explicit participant
access. IDs and UUIDs identify resources; they never grant access.

Every finance operation must authenticate the actor, verify active FullWorth Space or account access,
query the resource inside that authorized scope, and audit sensitive changes. An inaccessible
resource must not reveal its existence through a different response. Tests must prove that a user
cannot access another user's data by changing an ID.

## Browser authentication

FullWorth uses password authentication, passkeys, recovery codes and server-side revocable sessions.
Cookies are `HttpOnly`, secure in production and protected by appropriate SameSite rules.
Security-sensitive changes invalidate affected sessions. State-changing browser requests require
anti-forgery validation; browser credentials never belong in local storage.

External OAuth sign-in (Google and Apple) remains optional and is active only when provider
credentials are configured. External identities are linked only through verified email, follow the
same registration gate, and still pass through two-factor when enabled. The optional numeric PIN
app-lock only unlocks an already-authenticated session; it is never a primary credential and is
verified server-side with lockout protection.

Passkey RP ID and origins match `FULLWORTH_DOMAIN`. On a fresh instance, only the first account can
register by default; it becomes the instance administrator and registration then closes.

## Module and API safety

Public browser traffic uses same-origin BFF routes only. Internal keys are attached only after
destination validation, including when the destination is the unified host's loopback address.
Backend and Banking endpoint groups retain their dedicated key and user-context middleware.

Public endpoints use explicit response models and never expose raw provider data, internal paths,
session identifiers or secrets.

Banking keeps its own conservative synchronization, retry and provider-rate policies; see
[Banking safety](BANKING_SAFETY.md). The Banking module integrates Enable Banking and ING FinTS.
User-supplied bank credentials enter only the guarded Banking module path, are never returned to the
browser, and are persisted encrypted through the finance backend module.

## Transport, uploads and PWA

Production requires HTTPS, host allow-listing, HSTS, CSP, restrictive response headers and rate
limits. Internal loopback requests stay HTTP inside the process and are excluded from external HTTPS
redirection only when the remote address is loopback.

Receipt uploads validate size and type, use server-generated names and are served only after
authorization. The unified image includes the backend's Playwright/Chromium, Tesseract and Poppler
runtime so Amazon, receipt and PDF features keep the same isolation and validation behavior.

The PWA may cache static application assets but not finance data or receipts.

## Secrets, encryption and recovery

Normal self-hosted installations configure one stable `FULLWORTH_SECRET`. It supplies the database
and internal module credentials and deterministically derives the 32-byte field-encryption key.
Advanced compatibility overrides allow existing installations to retain separate credentials.

Changing `FULLWORTH_SECRET` with encrypted data already present requires a controlled re-encryption
migration.

Backups contain sensitive data, require narrow-scope access and must be tested through restore
verification. See [Operations](OPERATIONS.md).

## Release gate

Before exposing a deployment publicly, verify authentication and recovery, resource authorization,
CSRF and rate-limit behaviour, internal module key boundaries, response-data boundaries, security
headers, upload handling, dependency review and a successful restore test.
