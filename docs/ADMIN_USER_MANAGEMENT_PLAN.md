# Simple instance admin menu plan

Status: planned
Scope: generic FullWorth core feature. Hosted operator identity/private deployment data must not be committed here.

## Goal

Add a small instance-admin area for user management and account lifecycle operations.

Normal users:

- do not see an Admin navigation item
- cannot open the Admin route
- receive 403 from every Admin API endpoint
- cannot infer user lists or administrative state through the browser/BFF

Admins:

- get a dedicated Admin navigation item and /admin view
- can search/list users
- can inspect account lifecycle status only
- can disable/enable users
- can revoke user sessions
- can schedule the normal 7-day account deletion flow for a user
- can cancel a pending deletion while the recovery deadline has not passed
- can see failed/overdue deletion jobs
- can promote/revoke other admins with last-admin safeguards

The first version must NOT expose users' transactions, balances, receipts, contracts or other finance content.

## 1. Admin identity

Use an explicit generic instance-admin flag in the Web/Auth identity store:

- AuthUser.IsAdmin bool, default false

Reason:

- the current AuthDbContext uses IdentityUserContext without role tables
- one boolean is enough for the current single-role requirement
- it avoids coupling generic administration to the existing IntelligenceAdminGrant
- it keeps admin authorization local to the tier that owns login/session/user management

Migration:

- add IsAdmin boolean NOT NULL default false
- index is unnecessary initially

### Bootstrap

The first account created by FirstRunBootstrapper becomes IsAdmin = true.

Public registration always creates IsAdmin = false.

Invite-created users always create IsAdmin = false.

### Existing installs

Migration alone must not silently make everybody non-admin with no recovery path.

Startup bootstrap reconciliation:

1. if at least one AuthUser.IsAdmin exists -> do nothing
2. otherwise:
   - if Bootstrap:Email matches an existing auth account, grant that account admin
   - otherwise grant the oldest existing auth account admin and log a prominent warning

Do this once/idempotently.

## 2. Server-side authorization

Do not rely on hidden UI.

Add:

- InstanceAdminAuthorizer
- RequireInstanceAdmin endpoint filter/middleware or authorization policy

All /auth/admin/* endpoints require:

1. authenticated account
2. not disabled
3. not pending deletion
4. IsAdmin == true

Admin shell /admin also requires this check server-side.

Normal authenticated users get 403, not the admin HTML shell.

## 3. Current-user capability endpoint

Extend the existing current-user/session response or add:

GET /auth/capabilities

Response example:

{
  "admin": false
}

Do not expose a list of roles.

The main app uses this to decide whether to insert the Admin navigation entry.

### UI visibility

Do not render a permanently visible disabled Admin item.

At boot:

1. load capabilities
2. if admin == true, add/show Admin navigation item
3. otherwise no Admin entry exists in desktop sidebar or mobile More menu

Security remains server-side regardless of this visibility.

## 4. Admin route and screen

Route:

/admin

Keep it inside the existing FullWorth Web UI style.

Simple layout:

Admin
  Overview
  Users
  Deletions

For v1 these can be sections/tabs on a single page rather than separate complex modules.

### Overview

Four compact cards:

- Users total
- Active
- Disabled
- Pending deletion

Second row:

- overdue purges
- failed purges

No financial totals.

## 5. User list

Endpoint:

GET /auth/admin/users?search=&status=&limit=&cursor=

Default fields:

- AuthUserId
- FinanceUserId
- email
- createdAt
- updatedAt
- isAdmin
- isDisabled
- deletionRequestedAt
- deletionScheduledFor
- deletionLastError
- activeSessionCount
- lastSessionSeenAt

Optional Backend summary:

- spaceCount
- sharedSpaceCount

Do not return:

- balances
- transaction counts/details
- bank names/IBAN
- merchant/purchase content
- AI conversation content

### Filters

Simple filters only:

- All
- Active
- Disabled
- Pending deletion
- Admins

Search:

- email
- optionally FinanceUserId/AuthUserId exact match for support

Pagination mandatory. Never load the entire user table into the browser.

## 6. User detail drawer/page

Clicking a user opens a detail pane.

Sections:

### Account

- email
- created
- admin yes/no
- active/disabled
- AuthUserId / FinanceUserId in expandable technical section

### Sessions

- active session count
- device names
- last seen
- Revoke all sessions

Do not expose full IP history by default. If current Session UI already exposes current IP to the user, Admin can show only a shortened/current diagnostic representation if later needed.

### Spaces

Only metadata:

- number of personal spaces
- number of shared spaces
- optionally space names if support needs it

Do not display financial contents.

### Deletion

One lifecycle card:

Active
Disabled
Scheduled for deletion
Purge overdue
Purge failed

Actions change based on state.

## 7. Admin actions

### Disable user

POST /auth/admin/users/{authUserId}/disable

Behavior:

- set AuthUser.IsDisabled = true
- revoke all sessions
- deactivate matching FullWorthUser in Backend
- scheduled bank sync pauses through existing inactive-user behavior
- do not delete data

This is an administrative suspension, separate from deletion.

### Enable user

POST /auth/admin/users/{authUserId}/enable

Behavior:

- reject tombstoned/finally purged users
- set backend user active
- clear IsDisabled only after backend succeeds
- user can log in normally again

Do not implicitly cancel an existing deletion request. If pending deletion, Admin must use the explicit cancel-deletion action.

### Revoke sessions

POST /auth/admin/users/{authUserId}/revoke-sessions

Revoke all sessions for target.

No password reset in v1.

### Schedule deletion

POST /auth/admin/users/{authUserId}/schedule-deletion

Reuse the existing account deletion lifecycle:

- 7-day recovery window
- target account becomes pending deletion
- Backend inactive
- sessions revoked
- bank sync paused
- normal login reaches only deletion/recovery screen

Admin does not need the target user's password.

Require the ADMIN'S own recent re-authentication for destructive admin operations.

### Cancel deletion

POST /auth/admin/users/{authUserId}/cancel-deletion

Allowed only:

- before deletion deadline
- while no purge lease is active

Reuse the same backend reactivation path.

### Force purge

Do NOT put a normal "delete now" button in v1.

If eventually needed, put it under an Advanced/Danger section with:

- admin password/passkey re-auth
- typed target email confirmation
- dry-run preview
- impossible for the last admin/self unless another admin exists
- explicit irreversible warning

The standard admin action remains 7-day deletion.

## 8. Admin management

User detail for an admin can show:

- Grant admin
- Remove admin

Endpoints:

POST /auth/admin/users/{id}/grant-admin
POST /auth/admin/users/{id}/revoke-admin

Safeguards:

- cannot remove the last enabled admin
- cannot schedule/delete/disable the last enabled admin
- cannot accidentally revoke own admin when it would leave zero admins
- disabled/pending-deletion admins do not count as available recovery admins
- all admin-role changes require recent re-authentication

No hierarchy/super-admin in v1.

## 9. Recent admin re-authentication

Destructive operations should not trust an old unlocked browser session indefinitely.

Add an AdminAction re-auth flow:

POST /auth/admin/reauth

Input:

- current admin password initially
- later passkey support can be added

On success store a short server-side/session-scoped timestamp.

Suggested validity:

- 10 minutes

Require recent re-auth for:

- disable/enable another user
- schedule/cancel deletion
- grant/revoke admin
- future force purge

Simple read-only user listing does not need re-auth.

## 10. Generic admin audit log

Do not reuse IntelligenceAuditEvent for generic user administration.

Add a small Auth-side AdminAuditEvent table:

- Id
- ActorAuthUserId
- TargetAuthUserId nullable
- Action
- Outcome
- OccurredAt
- CorrelationId
- MetadataJson nullable, tightly bounded and non-sensitive

Actions:

- user.disabled
- user.enabled
- sessions.revoked
- deletion.scheduled
- deletion.cancelled
- admin.granted
- admin.revoked
- purge.retry_requested (if later added)

Do not store:

- passwords
- tokens
- full request bodies
- financial data
- receipt/transaction information

Admin UI can show latest 50-100 administrative events.

## 11. Deletion operations panel

The Admin screen should make the new purge worker observable.

GET /auth/admin/deletions

Groups:

Pending
- user
- requestedAt
- scheduledFor

Failed/overdue
- user
- scheduledFor
- deletionLastError
- last/next worker attempt if tracked

Admin actions:

- cancel before deadline
- retry purge after deadline/failure

Retry means clear/release a stale failed state and ask the worker to retry.
It must never bypass AccountPurgeService safety checks.

No raw SQL/admin manual database deletion button.

## 12. Optional purge dry-run

Very useful, but can be Phase 2.

GET /auth/admin/users/{id}/purge-preview

Return only counts/classifications:

- personalSpacesToDelete
- sharedSpacesToLeave
- sharedOwnershipTransfers
- storedFilesToDelete
- localAiRowsToDelete
- cloudOutboxRowsToDelete
- manifestSafe true/false
- blockers []

No transaction contents.

Show this automatically in the Admin deletion dialog.

## 13. Backend support endpoints

Web owns admin identity.

Browser must never call Backend admin lifecycle endpoints directly.

Web calls internal-key-only Backend endpoints such as:

- deactivate-user
- reactivate-user
- purge-user

Add a small internal support summary endpoint if needed:

POST /api/bootstrap/user-admin-summary

Input:

- financeUserIds[]

Response only operational metadata:

- active/tombstone
- space counts
- shared-space counts

This avoids N+1 calls and keeps finance contents private.

## 14. Intelligence admin consolidation

Do not block v1 on this.

Current IntelligenceAdminGrant can keep working.

Follow-up migration:

- make IntelligenceAdminAuthorizer recognize generic Instance Admin
- optionally retire separate grants later, or keep a separate "Intelligence manager" permission only if there is a real need

The generic User Admin menu must not be implemented inside Intelligence Admin.

## 15. Navigation

Desktop sidebar:

Admin item near Settings, visible only to admins.

Mobile:

Admin appears inside More rather than consuming a permanent bottom-nav slot.

Use a simple shield/admin icon consistent with the existing FullWorth icon language.

Routes:

/admin

Possible later deep links:

/admin?tab=users
/admin?tab=deletions
/admin?user=<authUserId>

No need for a separate admin SPA.

## 16. Safety rules

Mandatory:

- server-side admin check on every endpoint
- target IDs are never trusted without lookup
- CSRF on every mutation
- rate limit admin mutations
- recent admin re-auth for destructive actions
- revoke target sessions after disable/admin-triggered deletion
- last-admin invariant enforced transactionally
- no self-force-purge
- no financial data in user-management API
- no secrets/tokens in admin responses
- audit every mutation
- purge remains fail-closed through AccountPurgeService
- normal users get 403 even if they manually type /admin or call endpoint URLs

## 17. Tests

### Authorization

- anonymous /admin -> login
- normal authenticated user /admin -> 403
- normal user /auth/admin/users -> 403
- manually adding DOM/admin URL does not grant access
- admin can access shell + APIs

### Bootstrap/admin state

- first bootstrap auth user gets IsAdmin
- public registration never gets IsAdmin
- existing install with no admin gets one deterministic admin
- startup reconciliation is idempotent

### User management

- admin lists users with no financial details
- search/filter/pagination
- disable revokes sessions and backend access
- enable restores access
- session revoke works
- target pending deletion is represented correctly

### Deletion

- admin schedules same 7-day deletion state as self-service
- target sessions revoked
- target login goes to recovery screen
- user themselves may still reactivate during the window
- admin can cancel during window
- admin cannot cancel after purge lease/deadline
- purge failure appears in Admin Deletions

### Admin management

- grant admin
- revoke admin
- cannot disable/delete/revoke last enabled admin
- two-admin transition works
- re-auth expiration blocks destructive action

### Privacy

- list/detail responses contain no transaction/balance/bank/receipt data
- Admin audit metadata contains no credentials or financial payloads

## 18. Implementation order

Phase 1 - Admin foundation
- AuthUser.IsAdmin + migration
- first-run/existing-install admin reconciliation
- InstanceAdminAuthorizer / policy
- /auth/capabilities
- protected /admin shell
- conditional desktop/mobile navigation
- tests

Phase 2 - User management
- paginated admin user list
- user detail
- enable/disable
- revoke sessions
- generic AdminAuditEvent
- tests

Phase 3 - Deletion operations
- admin schedule deletion
- cancel deletion
- deletion queue/failure view
- reuse existing AccountDeletionService/worker
- tests

Phase 4 - Admin management + hardening
- grant/revoke admin
- last-admin invariant
- 10-minute admin re-auth
- optional purge dry-run
- optional manual retry
- migrate Intelligence Admin authorization toward generic instance admin

## V1 UI

Keep it deliberately small:

Admin
------------------------------------------------
[ 12 Users ] [ 10 Active ] [ 1 Disabled ] [ 1 Deleting ]

Users
[ Search... ]  [ All v ]

Julian@example.com       Admin      Active        >
alice@example.com                    Active        >
bob@example.com                      Deletes Sep 13 >

When a row opens:

Alice
alice@example.com
Created Sep 6, 2026

Status              Active
Sessions            2
Spaces              1 personal / 0 shared

[Revoke sessions]
[Disable account]

Danger zone
[Schedule deletion]

No charts and no finance-data previews in v1.

## Definition of done

The Admin menu is done when:

- normal users have no Admin nav item
- normal users receive 403 on Admin routes/APIs
- first instance admin is deterministic and recoverable
- admin user list is paginated/searchable
- admin can disable/enable and revoke sessions
- admin can schedule/cancel the safe 7-day deletion flow
- last enabled admin cannot be removed/disabled/deleted
- every mutation is audited
- financial contents are not exposed through user management
- deletion worker failures are visible to an admin
- the existing self-service deletion flow remains unchanged
