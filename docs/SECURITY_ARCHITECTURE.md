# Security architecture

## Boundaries

Only `FullWorth.Web` is publicly reachable. It is the authenticated web application and BFF.
`FullWorth.Backend`, `FullWorth.Banking` and PostgreSQL stay on the private network.

```text
Browser -> FullWorth.Web -> FullWorth.Backend -> PostgreSQL (finance schema)
                         -> PostgreSQL (auth schema)
                         -> FullWorth.Banking -> Enable Banking
                                              -> ING FinTS
FullWorth.Banking -> FullWorth.Backend (encrypted account/transaction ingest)
```

FullWorth.Web owns the authentication and session store and connects directly to its PostgreSQL auth
schema; FullWorth.Backend owns the finance schema. The two use distinct database credentials. The
browser never receives database, backend or banking credentials. FullWorth.Web derives the
authenticated user and FullWorth Space server-side, then makes guarded internal service calls.
FullWorth.Banking never persists finance data itself: it pushes encrypted account/transaction batches
back to FullWorth.Backend over a separate ingest credential.

## Identity and authorization

Users belong to one or more FullWorth Spaces. Accounts can additionally grant explicit participant
access. IDs and UUIDs identify resources; they never grant access.

Every finance operation must authenticate the actor, verify active FullWorth Space or account access,
query the resource inside that authorized scope, and audit sensitive changes. An inaccessible
resource must not reveal its existence through a different response. Tests must prove that a user
cannot access another user's data by changing an ID.

## Browser authentication

FullWorth.Web uses password authentication, passkeys, recovery codes and server-side revocable
sessions. Cookies are `HttpOnly`, secure in production and protected by appropriate SameSite rules.
Security-sensitive changes invalidate affected sessions. State-changing browser requests require
anti-forgery validation; browser credentials never belong in local storage.

External OAuth sign-in (Google and Apple) is optional and active only when its provider credentials
are configured. An external identity is linked to an existing account only by verified email, is
subject to the same registration gating, and still passes through two-factor when the account has it
enabled. A short numeric PIN app-lock is available as the fallback factor for the inactivity lock
(passkeys are primary): it only unlocks an already-authenticated session and is never a primary
credential. The PIN is verified server-side, stored PBKDF2-hashed (not in the browser), and repeated
wrong entries trigger a short lockout.

Passkey RP ID and origins must match the public HTTPS hostname. Public registration is not enabled;
the first account is created through the controlled bootstrap configuration.

## Service and API safety

Public browser traffic uses same-origin BFF routes only. Internal service credentials are attached
only after destination validation. Public endpoints use explicit response models and never expose
raw provider data, internal paths, session identifiers or secrets.

Banking has a separate credential and its own conservative rate and retry policy; see
[Banking safety](BANKING_SAFETY.md). FullWorth.Banking integrates two upstreams: Enable Banking
(PSD2/OAuth, per-user BYO application) and ING FinTS. User-supplied bank credentials — an Enable
Banking private key, or an ING FinTS login and PIN — enter only the internal FullWorth.Banking path,
are never returned to the browser, and are persisted encrypted by FullWorth.Backend.

## Transport, uploads and PWA

Production requires HTTPS, host allow-listing, HSTS, CSP, restrictive response headers and rate
limits. Receipt uploads validate size and type, use server-generated names and are served only after
authorization. The PWA may cache static application assets but not finance data or receipts.

## Secrets, encryption and recovery

Secrets are provided at runtime and must not be committed. Use distinct credentials for the databases
(the Web auth schema and the Backend finance schema), Web-to-Backend, Web-to-Banking, Banking-to-Backend
(ingest) and backups. The data-encryption key protects sensitive persisted fields; it must not be
replaced in place while encrypted data exists — that data must first be decrypted with the old key and
re-encrypted with the new one. See the secret-rotation procedure in [Operations](OPERATIONS.md).

Backups contain sensitive data, require narrow-scope access and must be tested through restore
verification. See [Operations](OPERATIONS.md).

## Release gate

Before exposing a deployment publicly, verify authentication and recovery, resource authorization,
CSRF and rate-limit behaviour, response-data boundaries, security headers, upload handling,
dependency review and a successful restore test.
