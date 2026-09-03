# Banking request safety

These rules are invariants for `FullWorth.Banking` and should not be weakened without reviewing the affected ASPSP/Enable Banking limits.

## Background access

Scheduled synchronization is treated as background data fetching. It intentionally sends no PSU headers.

- hard minimum between background attempts for one bank connection: 360 minutes
- default configured interval: 365 minutes
- default rate-limit cooldown: 365 minutes
- `LastAttemptAt` and `NextSyncAllowedAt` are persisted in `FullWorth.Backend`
- restarting containers therefore cannot bypass the cooldown
- user/UI refresh reads PostgreSQL through `FullWorth.Backend`; it does not contact banks
- manual `/api/banking/sync` respects exactly the same cooldown as the scheduled worker

## Concurrency and request pacing

- only one bank synchronization may run at a time
- simultaneous manual/scheduled sync requests are not queued into repeated bank runs
- Enable Banking HTTP calls are serialized
- default spacing between Enable Banking requests is 1000 ms
- account detail is fetched only on the first import, not on every refresh
- ongoing transaction sync uses the latest stored booking date minus the overlap window
- transaction continuation pages are followed sequentially and preserve the same query parameters

## Retries

`429 Too Many Requests` is never retried immediately.

When Enable Banking/ASPSP returns a rate-limit response:

1. stop the current bank sync
2. persist the failure state
3. respect `Retry-After` when it is later than the local cooldown
4. never retry before the local minimum cooldown

Only transient HTTP server/status failures are retried automatically (`408`, `500`, `502`, `503`, `504`). Retries are capped, use exponential delay with jitter, and still pass through the global request-spacing gate.

Authentication/authorization/client errors such as `400`, `401`, `403`, `404`, and `422` are not blindly retried.

## Transaction strategy

- first import: `strategy=longest`
- ongoing synchronization: `strategy=default`

The ongoing path is intentionally optimized for recent transactions rather than repeatedly requesting long history.

## Operational rule

Never add a "force sync" option that bypasses `NextSyncAllowedAt` for normal use. A future online/PSU-triggered fetch mode must be implemented separately and must correctly forward all PSU headers required by the selected ASPSP.
