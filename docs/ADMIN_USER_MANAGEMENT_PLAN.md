# Instance admin user management

Status: implemented on `main`.

This is a generic FullWorth core feature. Hosted operator identity and private deployment data do not belong here.

## Scope

The Admin area is deliberately limited to login/account administration.

Admins can see:

- auth user id
- email
- account created/updated timestamps
- active/disabled state
- instance-admin state
- TOTP/2FA enabled state
- session device names and last-seen timestamps
- account-deletion lifecycle state and technical purge error code

Admins cannot see through the Admin API/UI:

- FullWorth spaces or space names
- bank accounts or bank connections
- IBAN/account identifiers
- balances
- transactions
- categories/merchants/purchases
- receipts/documents
- contracts
- assets/liabilities
- AI conversations
- any other finance content

The Admin implementation must stay independent from finance-content support tooling.

## Admin identity

`AuthUser.IsAdmin` is the single instance-admin flag.

Startup reconciliation is idempotent:

1. if an admin already exists, do nothing
2. otherwise prefer the existing account matching `Bootstrap:Email`
3. otherwise choose the oldest existing auth account

Public registration and invite-created accounts are not made admins automatically.

The last operational admin cannot be:

- disabled
- stripped of admin rights
- scheduled for deletion
- self-deleted through the ordinary deletion flow

## Authorization

The UI is not the security boundary.

- normal users do not see the Admin navigation entry
- `/admin` checks instance-admin state server-side
- `/admin/*` static assets are also denied to normal users
- every `/auth/admin/*` API endpoint independently checks instance-admin state
- normal authenticated users receive HTTP 403

The normal app discovers only:

`GET /auth/capabilities`

with the small capability response:

- `admin`
- `twoFactorEnabled`

## Admin UI

The Admin area is a standalone `/admin` screen. It does not load the normal FullWorth finance APIs.

Overview shows only auth/account counts:

- users
- active
- disabled
- pending deletion
- failed/overdue deletion
- admins

User list supports:

- search by email
- exact AuthUserId lookup
- filters: all / active / disabled / deleting / admins
- pagination

User detail shows only account metadata and sessions.

## Actions

Admins can:

- disable a user
- enable a user
- revoke all sessions
- grant instance-admin
- revoke instance-admin
- schedule the normal 7-day account deletion
- cancel deletion before the recovery deadline/purge lease

Admin-triggered deletion reuses the same `AccountDeletionService` as self-service deletion.

There is no immediate/force-purge button in v1.

## Account deletion

Scheduling deletion:

- deactivates the backend finance identity
- pauses scheduled banking through the existing inactive-user behavior
- revokes sessions
- starts the standard seven-day recovery period

The user may still log in during the recovery period and reactivate through the deletion-recovery screen.

After the deadline the existing fail-closed purge worker performs the final cleanup.

## Admin audit

Generic admin mutations are written to the Auth database as `AdminAuditEvent`:

- actor AuthUserId
- optional target AuthUserId
- action
- outcome
- timestamp

The admin audit stores no financial payload, password, TOTP key, token or request body.

## TOTP two-factor authentication

FullWorth supports ordinary TOTP authenticator apps, including Google Authenticator.

Settings -> Security -> Two-factor authentication:

1. FullWorth creates an authenticator key
2. the user adds the setup key to Google Authenticator or another TOTP app
3. the user confirms with the generated 6-digit code
4. `TwoFactorEnabled` is enabled in ASP.NET Identity

Login becomes:

1. email + password
2. if TOTP is enabled, ask for the current 6-digit authenticator code
3. create the FullWorth session only after both succeed

The temporary email/password pair needed between login step 1 and 2 exists only in the login page's JavaScript memory. It is not written to localStorage, sessionStorage or IndexedDB.

Disabling TOTP requires a current TOTP code and resets the authenticator key.

No extra custom admin re-authentication layer is used in v1.

## Main endpoints

Capabilities:

- `GET /auth/capabilities`

TOTP:

- `GET /auth/two-factor/status`
- `POST /auth/two-factor/setup`
- `POST /auth/two-factor/enable`
- `POST /auth/two-factor/disable`

Admin:

- `GET /auth/admin/overview`
- `GET /auth/admin/users`
- `GET /auth/admin/users/{id}`
- `POST /auth/admin/users/{id}/disable`
- `POST /auth/admin/users/{id}/enable`
- `POST /auth/admin/users/{id}/revoke-sessions`
- `POST /auth/admin/users/{id}/schedule-deletion`
- `POST /auth/admin/users/{id}/cancel-deletion`
- `POST /auth/admin/users/{id}/grant-admin`
- `POST /auth/admin/users/{id}/revoke-admin`
- `GET /auth/admin/audit`

## Tests

The suite now covers or contains guards for:

- normal user receives 403 from Admin shell/API/assets
- Admin user list excludes finance-data fields
- last operational admin cannot be disabled/demoted/deleted
- TOTP-enabled account does not receive a login session after password alone
- wrong TOTP is rejected
- current valid TOTP completes login
- auth migration contains `IsAdmin` and `AdminAuditEvents`
- TOTP login form is present and does not persist the pending password in browser storage

The broader account-deletion test suite separately covers the seven-day recovery and final purge behavior.
