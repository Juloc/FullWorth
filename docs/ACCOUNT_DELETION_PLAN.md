# Account deletion and recovery plan

Status: planned
Scope: generic FullWorth core feature. No hosted-operator identity or private deployment data belongs here.

## Goal

Provide a simple, recoverable account deletion flow that is safe for personal finance data:

1. User requests deletion from Settings.
2. Account enters a 7-day pending-deletion period.
3. Normal FullWorth usage stops immediately and bank sync is paused.
4. The user can still sign in to a dedicated deletion-status screen and reactivate with one click.
5. After the 7-day deadline, a background purge irreversibly removes personal data.
6. A purge failure must never delete only part of the identity and leave orphaned personal data.

The implementation must be fail-closed: if FullWorth cannot prove that all relevant data is covered, final purge must stop and retry later.

## State model

Use explicit persisted state on the Web auth user:

- Active
  - DeletionRequestedAt = null
  - DeletionScheduledFor = null
- PendingDeletion
  - DeletionRequestedAt != null
  - DeletionScheduledFor = DeletionRequestedAt + 7 days
- Purging
  - internal transient/lease state so two workers cannot purge the same account concurrently
- PurgeFailed
  - account remains blocked, data remains recoverable only until a purge has actually started successfully
  - worker retries and records the failure without deleting the auth identity
- Purged
  - no auth user remains; finance identity is anonymized or removed as described below

Do not use a soft-delete flag as the final state. The 7-day state is the soft-delete/recovery window; after it expires, personal data should actually be removed or irreversibly anonymized.

## 1. Auth model

Add to AuthUser:

- DateTimeOffset? DeletionRequestedAt
- DateTimeOffset? DeletionScheduledFor
- DateTimeOffset? DeletionLeaseUntil (or equivalent worker lock)
- string? DeletionLastError (short bounded technical code, no sensitive payload)

Add an EF migration.

Keep the existing Identity claims for accepted Terms/Privacy/18+ evidence. They are deleted automatically when the AuthUser is finally deleted.

## 2. User-facing flow

Settings -> Account -> Delete account.

Deletion request requires:

- an authenticated session
- current password re-authentication initially
- an explicit destructive confirmation in the dialog
- clear text that deletion becomes irreversible after 7 days

Endpoint:

POST /auth/account-deletion/request

On success:

- persist DeletionRequestedAt and DeletionScheduledFor
- revoke all other sessions
- keep the current session only for the deletion-status experience
- call the Backend internal deactivation endpoint
- redirect to /account/deletion

No email is required for the recovery flow.

### Pending-deletion screen

Route:

GET /account/deletion

Show:

- deletion requested timestamp
- irreversible purge deadline
- remaining time
- Reactivate account
- Log out

While pending deletion, normal app pages and BFF endpoints are blocked by Web middleware. Only the small allowlist required for login, logout, deletion status and reactivation remains reachable.

A user who logs in during the 7-day period is always redirected to /account/deletion rather than the finance UI.

## 3. Reactivation

Endpoint:

POST /auth/account-deletion/cancel

Behavior:

1. Require authenticated pending-deletion user.
2. Call Backend internal reactivation endpoint first.
3. Clear deletion state in AuthUser only after Backend succeeded.
4. Revoke stale sessions and issue/continue a clean active session.
5. Redirect to FullWorth.

Because provider sessions are retained during the buffer, bank connections can resume without forcing the user to reconnect.

Reactivation must be idempotent.

## 4. Backend active state and bank-sync pause

Use the existing FullWorthUser.IsActive flag as the Backend access gate.

Internal-key-only endpoints under /api/bootstrap:

- POST /api/bootstrap/deactivate-user
- POST /api/bootstrap/reactivate-user
- POST /api/bootstrap/purge-user

These endpoints are server-to-server only and must never be reachable through the authenticated browser BFF.

### Deactivate

Set FullWorthUser.IsActive = false.

The existing InternalUserContextMiddleware already rejects inactive users, which immediately blocks ordinary finance API access.

### Scheduled bank sync

Update the internal BankConnectionStore list used by scheduled banking sync so connections authorized by an inactive FullWorthUser are omitted.

Rules:

- AuthorizationUserId == null: retain existing compatibility behavior.
- AuthorizationUserId points to active user: sync normally.
- AuthorizationUserId points to inactive user: do not schedule sync.

Do not delete or revoke the Enable Banking provider session during the 7-day window. This makes reactivation cheap and reversible.

Manual sync/connect is already blocked because the inactive finance user cannot pass Backend authorization.

## 5. Final purge worker

Run a small background service in FullWorth.Web, because the deletion schedule lives in the auth database.

Suggested cadence: hourly.

For each due user:

1. Atomically acquire a short purge lease.
2. Call Backend /api/bootstrap/purge-user using FinanceUserId.
3. Backend completes its purge transaction.
4. Only after Backend returns a durable success, delete the AuthUser.
5. Identity cascades remove sessions, recovery codes, passkeys, claims and other auth-owned records.
6. Log a non-sensitive completion event.

If step 2 fails:

- do NOT delete AuthUser
- do NOT clear the pending state
- record a bounded error code
- retry later

This ordering prevents the dangerous state where login identity is gone while finance data still exists with no owner able to recover or inspect it.

## 6. Finance-data purge rules

Deletion must distinguish personal and shared spaces.

### A. User is the only member of a space

Treat the space as personal to the deleting user.

Delete all data owned by that FullWorthSpace, including at minimum:

- bank connections and encrypted provider/session data
- Enable Banking profile linkage owned by the user
- accounts and account-owner grants
- transactions, allocations, transfer links and balance snapshots
- budgets
- contracts and candidates
- purchases, receipts, receipt documents, articles and import jobs
- categories/rules/merchant mappings that are space-owned
- notifications/dedup state
- assets, valuations, real estate, vehicles, metals and related documents
- loans/investments and supporting histories
- tax-assistant data
- coach/spending-review data
- audit records for the deleted personal space
- invites and sharing records
- any other current or future FullWorthSpace-owned table

Finally remove:

- FullWorthSpaceMembers
- FullWorthSpace
- the user's per-space preferences

### B. Space is shared with other users

Do not delete the shared space or other users' finance data.

Remove only the departing user's access and user-owned secrets/state:

- AccountOwner rows for the deleting user
- FullWorthSpaceMember row
- pending invites created specifically for/by the user where applicable
- user preferences
- push devices
- Enable Banking profiles owned by that user
- bank connections whose AuthorizationUserId is the deleting user:
  - close provider-side session if possible during final purge
  - remove the connection only if doing so cannot delete financial data owned/shared by remaining members
  - otherwise strip/revoke the deleted user's authorization secret and leave shared historical data intact

Historical audit rows in a shared space may need to remain for integrity. They must no longer resolve to identifying account information after the finance user is anonymized.

### Finance user tombstone

Do not force-delete FullWorthUser if Restrict relationships intentionally preserve historical integrity.

After all personal spaces and direct user-owned data are purged:

- EmailNormalized -> unique non-routable tombstone value derived only from the GUID, e.g. DELETED-{guid}@invalid.fullworth
- DisplayName -> "Deleted user"
- IsActive -> false
- clear onboarding/personal preference fields where possible
- retain only the stable GUID required by historical foreign keys

There must be no remaining mapping from that GUID to the former email/name in FullWorth databases.

Important: a tombstone is a technical referential-integrity placeholder, not an analytics identity.

Rules:

- every deleted user keeps a distinct internal tombstone GUID; never collapse multiple deleted users into one shared "Deleted user" record
- user-level statistics, leaderboards, personal aggregates, cohort views and dashboards must exclude tombstone users by default
- historical rows that still reference a tombstone may only be included in space/global aggregates when the metric is explicitly defined as user-independent
- any aggregation grouped by UserId must filter inactive/tombstone users unless the query is an explicit internal integrity/audit report
- no UI should present multiple deleted users as if they were one person merely because the display label is the same
- analytics projections should expose a dedicated IsDeleted/IsTombstone state internally instead of inferring deletion from the display name
- tests must prove that two deleted users never merge into the same statistical bucket

This avoids weakening existing Restrict foreign keys globally just to support deletion while keeping deleted identities out of normal analytics.

## 7. Purge manifest: mandatory safety guard

Do not rely on a hand-written delete list without verification.

Create a central PersonalDataPurgeManifest that classifies every application table/entity into one of:

- PersonalSpaceData: delete when purging a sole-member space
- SharedUserLink: remove only target user's row/link
- SharedHistorical: retain but ensure user identity is anonymized
- GlobalAnonymous: not personal to an account and retained
- System: migrations/configuration/system rows

Add a CI test that enumerates the EF models for:

- FullWorthDbContext
- IntelligenceDbContext
- AuthDbContext

and fails when any mapped application entity/table is not classified.

Result: adding a new feature/table in the future forces the developer to explicitly decide how account deletion handles it before CI can pass.

This is the main protection against silent data-retention regressions.

## 8. Transaction strategy

Backend purge is one logical operation but should avoid a huge all-or-nothing transaction if the database becomes large.

Use:

1. preflight/classification check
2. one transaction per personal space
3. one transaction for user-owned/shared-link cleanup
4. final finance-user anonymization transaction

Each stage is idempotent so a retry can continue safely.

The Web AuthUser is deleted only after every Backend stage reports complete.

Never use TRUNCATE, wildcard schema deletion, dynamic "delete every table" SQL, or global cascade changes in production account deletion.

## 9. Intelligence Cloud

Local instance:

- remove unsent outbox items that are directly attributable to the deleting user
- revoke/remove user-specific consent evidence if consent is user-specific
- do not disable an instance-wide Cloud feature solely because one member deletes their account

Remote FullWorth Cloud:

- already accepted genuinely anonymized/aggregated merchant/product/category intelligence may remain
- no remote record may retain a link back to the deleted FullWorth user
- if a remote payload can still identify or correlate the user, provide a deletion/tombstone call keyed by opaque contribution id

Add tests proving the cloud keeps only the intended anonymous/global statistics.

## 10. Backups

The 7-day recovery feature must not depend on restoring backups.

During the first 7 days nothing is physically purged, so reactivation is a normal state transition.

After purge:

- deleted data can still exist transiently in normal server/database backups until those backups expire
- backups must not be selectively modified
- restored backups must not be used to resurrect an account after the deletion deadline except for disaster recovery under controlled procedures

Document the actual hosted backup retention separately in deployment/private legal documentation, not in the public core repository.

## 11. UI details

Settings card:

Account
- Export data
- Delete account

Deletion dialog:

- explain 7-day recovery period
- explain normal access and bank sync stop immediately
- current-password field
- explicit destructive confirmation
- final button: "Delete account"

Pending screen:

"Account scheduled for deletion"
"Your data will be permanently deleted on <date>."
[Reactivate account]
[Log out]

No countdown that updates every second; date + whole days/hours is enough.

## 12. Security rules

- CSRF protection on request/cancel endpoints
- password re-authentication for deletion request
- rate-limit destructive endpoints
- do not accept FinanceUserId from the browser; derive it from authenticated AuthUser
- internal Backend purge endpoints require internal key and explicit server-side identifiers
- purge worker uses a lease to prevent duplicate execution
- every purge step is idempotent
- no secret values in logs
- audit deletion request/cancel/purge with IDs/timestamps only

## 13. Tests

Required tests:

### Auth/Web
- request requires authenticated user
- request requires correct current password
- request records exactly 7-day deadline
- other sessions are revoked
- pending user is redirected to deletion page
- BFF is blocked while pending
- pending user can log in
- cancel reactivates
- cancel after final purge is impossible
- purge worker never deletes AuthUser if Backend purge fails
- two workers cannot purge same user simultaneously

### Backend
- inactive user is rejected by normal API middleware
- scheduled banking excludes inactive authorizing users
- reactivation restores API/sync eligibility
- sole-member space purge deletes all classified personal-space data
- shared-space purge preserves other members and shared finance data
- user-owned links/secrets are removed from shared spaces
- finance user is irreversibly anonymized
- purge is idempotent

### Manifest
- every mapped application entity is classified
- intentionally adding an unclassified fake/new entity makes the guard fail
- tombstone users are excluded from normal user-level analytics
- two different deleted-user GUIDs never collapse into one analytics identity

### Cloud
- user-correlatable pending data is removed
- anonymous/global intelligence remains
- no deleted FinanceUserId/email is retained in outbound payloads

## 14. Deployment defaults

Generic defaults:

- AccountDeletion:RecoveryWindow = 7.00:00:00
- AccountDeletion:PurgeInterval = 01:00:00
- AccountDeletion:PurgeLease = 00:15:00

Allow operators to configure the recovery window, but keep 7 days as the product default.

Do not allow a zero-day destructive deletion through the normal UI. An admin-only emergency purge, if ever added, should be a separate explicitly dangerous operation.

## 15. Implementation order

Phase 1 — reversible deletion state
- Auth model + migration
- request/cancel endpoints
- pending-deletion UI
- Web access gate
- Backend deactivate/reactivate
- bank-sync active-user filter
- tests

Phase 2 — purge safety
- PersonalDataPurgeManifest
- CI classification guard
- explicit personal-space/shared-user purge service
- finance-user anonymization
- exhaustive seeded integration tests

Phase 3 — worker
- purge lease
- hourly worker
- Backend purge call
- AuthUser final deletion
- retry/error handling

Phase 4 — Cloud and polish
- local/remote cloud deletion handling
- Settings UX
- export-before-delete shortcut
- legal copy alignment

## Definition of done

Account deletion is done only when all are true:

- request immediately blocks normal use
- scheduled bank sync stops without destroying provider credentials during the buffer
- login during the buffer reaches only the recovery page
- reactivation restores the same account and data
- after 7 days the purge executes without manual intervention
- a Backend purge failure cannot delete the Auth identity
- personal sole-member spaces are actually removed
- shared users/spaces are not damaged
- identifying user fields are gone after purge
- CI fails for any new unclassified data entity
- private hosted-operator data is not required by or committed to the public core
