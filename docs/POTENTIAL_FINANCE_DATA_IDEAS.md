# Potential finance data ideas

> **Status: ideas only. This is not a backlog, roadmap, commitment, or implementation plan.**
>
> Items in this document are candidates for discussion. Before any work starts, FullWorth should
> decide **whether the idea is useful at all and, if so, how it should behave**. Some ideas may be
> rejected, merged with another concept, or implemented in a much smaller form.

These ideas build on the existing exact finance timestamps, account data freshness, bank sync
history, and transaction status observation history.

## Data freshness and sync transparency

### Separate "last checked" from "last successfully synced"

Potentially show both the latest attempted provider check and the latest successful data update.

Questions to resolve:

- Does this add useful clarity or merely more timestamps?
- Should it be connection-level, account-level, or both?
- What exactly counts as "checked" if the provider returns only partial data?
- How should manual accounts and imports behave?

### Sync statistics per run

Potentially record compact counts such as accounts checked, transactions inserted/updated,
balances refreshed, partial failures, and duration.

Questions to resolve:

- Which counts are useful to users rather than just diagnostics?
- Which metrics can be collected consistently across Enable Banking, FinTS and future providers?
- How much detail should be retained and for how long?
- Should technical details be visible by default or only on demand?

### Data freshness per data type

Potentially track freshness separately for balances, transactions, depot data, contracts or other
data instead of implying that one timestamp describes the whole account.

Questions to resolve:

- Which data types need their own freshness semantics?
- Can every provider supply enough information to make this accurate?
- Should freshness mean "provider queried", "data changed", or "FullWorth observed data"?

### Freshness warnings

Potentially warn when financial data has become unexpectedly old.

Questions to resolve:

- What is "stale" for each account/provider type?
- How should provider cooldowns, TAN requirements, weekends and temporary outages affect warnings?
- Should warnings be informational, yellow/red, or entirely absent unless action is required?

## Transaction history and traceability

### Broader transaction change history

Potentially record meaningful changes to amount, merchant, description, category, dates or other
fields with timestamps and before/after values.

Questions to resolve:

- Which changes are financially meaningful enough to retain?
- Which changes are provider observations versus FullWorth/user edits?
- How much historical data is appropriate from a privacy and storage perspective?
- Should technical/provider-only changes be hidden from normal users?

### Unified transaction timeline

Potentially combine status observations, categorization, manual changes and other meaningful events
into one chronological timeline in transaction details.

Questions to resolve:

- Which event types belong in a user-facing timeline?
- Should some audit events remain admin/technical only?
- How should noisy automated events be condensed?

### Explain "why this transaction looks like this"

Potentially expose selected provenance information such as source/provider, matching method,
categorization source, import origin and last meaningful update.

Questions to resolve:

- Which provenance fields help normal users understand a transaction?
- Which identifiers are too technical or sensitive to expose?
- Should this be a compact explanation or an expandable technical section?

## Pending transaction handling

### Match pending transactions to final booked transactions

Some banks/providers may replace a pending entry with a new booked entry rather than updating the
same provider identifier. FullWorth could potentially link those records.

Questions to resolve:

- Which matching signals are reliable enough to avoid false links?
- How much amount/date/merchant variation should be tolerated?
- Should uncertain matches require confirmation?
- Should the original pending record remain separately auditable?

### Detect pending transactions that disappear

A pending transaction may stop being returned without ever becoming booked.

Questions to resolve:

- After how many successful syncs can a pending transaction be considered gone?
- Is "expired", "cancelled", "not returned anymore", or another state the correct semantic?
- How should temporary provider gaps be distinguished from a real disappearance?

## Connection and provider diagnostics

### Account-level sync history

Potentially supplement connection-level history with the result for each individual account.

Questions to resolve:

- Is this useful enough to justify the additional data and UI complexity?
- How should one partially failing account affect the connection-level result?
- Should depot/cash accounts use the same model?

### Friendly error plus machine-readable error

Potentially show a short human explanation while retaining a stable machine code for diagnostics.

Questions to resolve:

- Which errors can be translated reliably without hiding important provider meaning?
- Which technical details should only be shown on demand?
- How should provider-specific error codes be normalized?

### Provider timestamps when genuinely available

If a provider supplies a real time-of-day for a financial event in the future, FullWorth could
store it separately from date-only booking/value dates.

Questions to resolve:

- Does the provider define the timestamp semantics clearly?
- Is the timezone known?
- Should it be a new provider timestamp field instead of changing existing date-only fields?

## Import traceability

### Import history comparable to bank sync history

Potentially record when an import ran, its source/type, counts, duration and result.

Questions to resolve:

- Which metadata is safe and useful to retain?
- Should original filenames be stored, sanitized, hashed, or omitted?
- Should rollback/reconciliation actions appear in the same history?
- Can CSV, Finanzguru, depot and future imports share one model?

## Presentation ideas

### Relative time in addition to exact timestamps

Potentially show values such as "12 minutes ago" while preserving the exact timestamp nearby or in
a tooltip/detail view.

Questions to resolve:

- Where does relative time improve scanning?
- Where is an exact timestamp mandatory?
- How often should relative labels refresh in the browser?

### Explicit timezone context

Potentially make the display timezone clearer, especially when users travel or inspect provider
timestamps.

Questions to resolve:

- Should FullWorth always use browser-local time, a user-selected timezone, or the FullWorth Space
  timezone?
- Which timestamps should retain/display the provider timezone separately?

## Decision rule

An item should move from this document into a roadmap, issue, implementation plan or code change
only after its intended user value and semantics are clear enough to answer:

1. **Do we want this behavior?**
2. **What exact problem does it solve?**
3. **What is the smallest useful version?**
4. **What are the data-quality, privacy and provider-specific risks?**
5. **How should it behave when source data is incomplete or ambiguous?**

Until then, inclusion here means only **"potential idea worth discussing"**.
