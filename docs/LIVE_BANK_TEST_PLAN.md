# FullWorth — Live Bank Validation Plan

A precise, operator-run checklist for validating real bank connections against a deployed FullWorth
instance. Run the banks **in this order** (each builds confidence for the next):

1. **DKB** (German retail bank — booked/pending SEPA baseline)
2. **ING** (second German bank — confirms multi-connection isolation)
3. **PayPal** (wallet-style, many small transactions, unusual counterparties)
4. **C24** (C24 Bank / smart transactions)
5. **Revolut** (multi-currency, frequent pending→booked churn)

> **Safety.** This is read-only validation of *your own* accounts. Do **not** run destructive or
> load/attack testing against live bank or Enable Banking services. Respect the background sync
> floor (≥ 360 min between automatic syncs) and the per-connection cooldown; use manual sync
> sparingly. Enable Banking sandbox/credentials and the RSA key must already be configured
> (`docs/OPERATIONS.md`). Capture screenshots/logs, not raw access tokens, when recording results.

## Prerequisites (once)

- [ ] Deployed instance reachable over HTTPS at your `FULLWORTH_PASSKEY_ORIGIN`.
- [ ] `ENABLE_BANKING_APPLICATION_ID` set and the RSA key staged as the docker secret.
- [ ] The Enable Banking application has your `…/connect/enable-banking/callback` redirect URL.
- [ ] You are signed in as an owner of the FullWorth Space under test.
- [ ] Note the current UTC time and the configured `Sync__MinimumBackgroundSyncIntervalMinutes`
      (default 365) and `Sync__RateLimitCooldownMinutes` so cooldown expectations are exact.

## Per-bank checklist

Repeat the whole block for each bank, in the order above. Record PASS/FAIL + notes per line.

### A. Connect / consent
- [ ] Start a connection for the institution; complete the bank's consent/redirect flow.
- [ ] Land back on FullWorth via the callback with the connection shown as **AUTHORIZED**.
- [ ] Consent-health status reads *authorized* (not `reauthorization_required`/`expired`), and a
      sensible `daysUntilExpiry` is shown.
- [ ] Accounts from the bank are discovered and listed under the FullWorth Space.

### B. Initial history
- [ ] First sync backfills history up to `Sync__InitialHistoryDays` (default 180).
- [ ] Transaction dates, amounts and currency match the bank's app/statement for a spot sample.
- [ ] Expense/income signs are correct (expenses negative), counterparties populated.

### C. Balances
- [ ] Account balance(s) match the bank for each connected account.
- [ ] Net worth / dashboard reflects the new balances in the space's base currency.
- [ ] Multi-currency (Revolut): each currency balance is correct and not silently converted.

### D. Pending → booked lifecycle
- [ ] A pending (PDNG) transaction appears while pending and is flagged as pending.
- [ ] After the bank books it, a **later sync reconciles** the pending entry to the booked one
      (no duplicate; the pending row is replaced/updated, not left orphaned).
- [ ] Analytics/budgets exclude pending where expected and include booked once reconciled.

### E. Background cooldown
- [ ] After a sync, an automatic background sync does **not** run again before the 360-minute hard
      floor / configured interval has elapsed.
- [ ] Connection shows its `nextSyncAllowedAt` / cooldown correctly.

### F. Manual sync during cooldown
- [ ] Trigger a manual sync while in cooldown → result is **`cooldown`** (or `already_running`),
      not an error, and no extra provider call is made beyond policy.
- [ ] After cooldown elapses, a manual sync returns **`started`** and pulls new activity.

### G. Reconnect before / after expiry
- [ ] Before expiry: re-authorize/refresh consent succeeds and keeps the same accounts/history
      (no duplicate accounts or transactions).
- [ ] After (or simulated) expiry: status becomes `reauthorization_required`/`expired`; the
      reconnect flow restores it to AUTHORIZED and resumes syncing without data loss.

### H. Restart safety
- [ ] Restart the stack (`docker compose restart`) mid-lifecycle: no duplicate transactions on the
      next sync, cooldown/next-run state survives, cookies/sessions survive (durable Data Protection
      key ring), and no re-backfill of already-imported history.

## Cross-bank checks (after ≥ 2 banks connected)
- [ ] **Isolation:** a member who does not own a given account cannot see its transactions
      (per-account authorization holds across connections).
- [ ] **No cross-contamination:** each transaction is attributed to the correct account/bank.
- [ ] **Error classification:** if a provider returns rate-limit/consent-expired/transient errors,
      the connection surfaces the correct category and backs off (does not hammer the provider).

## Recording results

| Bank | A | B | C | D | E | F | G | H | Notes |
|------|---|---|---|---|---|---|---|---|-------|
| DKB     |  |  |  |  |  |  |  |  |  |
| ING     |  |  |  |  |  |  |  |  |  |
| PayPal  |  |  |  |  |  |  |  |  |  |
| C24     |  |  |  |  |  |  |  |  |  |
| Revolut |  |  |  |  |  |  |  |  |  |

Any FAIL: capture the connection id, the sync result/status, the relevant `fullworth-banking`
logs (with tokens redacted), and the audit-log entries for the connection, then file an issue.
