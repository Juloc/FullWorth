# Banking request safety

These rules are invariants for `FullWorth.Banking` and should not be weakened without reviewing the
affected ASPSP/Enable Banking limits.

## Background access

Scheduled synchronization is background data fetching and intentionally sends **no PSU headers**.

- hard minimum between background attempts for one bank connection: 360 minutes
- default worker wake-up interval: 15 minutes; this only checks which connections are due
- default provider rate-limit cooldown: 360 minutes
- `LastAttemptAt` and `NextSyncAllowedAt` are persisted in `FullWorth.Backend`
- restarting containers therefore cannot bypass the cooldown
- UI refreshes read PostgreSQL and never contact a bank

## User-triggered online access

A manual sync is different from background polling. When the authenticated end user triggers a fetch,
FullWorth.Web derives the PSU context from the real HTTP request and the Banking service forwards it to
Enable Banking.

Supported values:

- `Psu-Ip-Address`
- `Psu-User-Agent`
- `Psu-Referer`
- `Psu-Accept`
- `Psu-Accept-Charset`
- `Psu-Accept-Encoding`
- `Psu-Accept-language`

`Psu-Geo-Location` is not forwarded unless a future explicit geolocation-consent feature is added.

If an ASPSP reports `required_psu_headers`, FullWorth sends either the complete required set or no PSU
headers at all. A partial set must never be sent because Enable Banking returns
`PSU_HEADER_NOT_PROVIDED` for that case.

A user-triggered sync may bypass the ordinary 6-hour background cadence because it is an online fetch.
It may **not** bypass a persisted provider rate-limit window.

## Concurrency and request pacing

- only one bank synchronization runs at a time
- simultaneous manual/scheduled sync requests are not queued into repeated provider runs
- Enable Banking HTTP calls are serialized
- default spacing between Enable Banking requests is 1000 ms
- account details are fetched on initial import or when session metadata is insufficient
- ongoing transaction sync uses the latest successfully stored booked date minus the overlap window
- continuation pages are followed sequentially with the same retrieval mode

## Retries and rate limits

`429 Too Many Requests` is never retried immediately.

When Enable Banking/ASPSP returns `ASPSP_RATE_LIMIT_EXCEEDED`:

1. stop the current bank sync
2. persist the failure state
3. honor `Retry-After` when it is later than the local cooldown
4. otherwise wait at least six hours
5. never let a manual force flag bypass this provider-imposed window

Only transient HTTP failures are retried automatically (`408`, `500`, `502`, `503`, `504`).
Retries are capped, use delay/jitter and still pass through the global request-spacing gate.

## Transaction strategy

- first import: `strategy=longest`, **without** `date_from` or `date_to`
- continue until no `continuation_key` is returned, even if an intermediate page is empty
- ongoing synchronization: `strategy=default`
- ongoing `date_from` = latest stored BOOK booking date minus overlap (default 7 days)
- `WRONG_TRANSACTIONS_PERIOD` is handled by a single bounded fallback rather than retrying the same
  invalid range forever

FullWorth imposes no artificial historical lower bound on the initial import.

## Transaction identity

- durable provider identity is account `identification_hash` + transaction `entry_reference`
- `transaction_id` is only a pointer to the provider transaction-details endpoint and may change
- when `entry_reference` is missing, use an account-scoped deterministic fallback fingerprint
- never merge pending/booked transactions solely because amount/date/payee look similar

## Consent lifecycle

Removing a bank connection must go through `FullWorth.Banking`:

1. call Enable Banking `DELETE /sessions/{session_id}`
2. tolerate already-closed/missing provider sessions
3. only then remove the local connection/data

There is deliberately no browser-accessible backend delete route that can bypass remote consent close.
