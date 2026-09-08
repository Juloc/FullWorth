# AI Autopilot Release Smoke Checklist

Baseline for the deployable Autopilot rollout.

## Current migration heads

Main finance migrations:

- `20260906231500_ContractMerges`

Intelligence migrations:

- `20260907210000_FinancialSignals`

Deploy 0 and Deploy 1 add **no database migration**. Deploy 2 adds only the additive Intelligence tables `FinancialSignals` and `FinancialSignalStates`.

## Before merge

- [ ] `dotnet build FullWorth.slnx --configuration Release`
- [ ] Autopilot backend guard tests pass
- [ ] Web architecture guard tests pass
- [ ] affected existing Coach/Intelligence tests pass
- [ ] no required Docker Compose or environment change
- [ ] no new container
- [ ] generic Autopilot actions remain `off`; only explicit contract merge execution is enabled

## After deploy

### Process / health

- [ ] all existing containers start
- [ ] backend migration startup completes
- [ ] backend `/health` returns `status=ok`
- [ ] no repeated migration/worker exception loop in logs

### Core application

- [ ] login succeeds
- [ ] dashboard opens
- [ ] accounts open
- [ ] transactions open
- [ ] contracts open
- [ ] budgets open
- [ ] analytics open
- [ ] net worth opens

### Finance mutations

- [ ] a normal transaction edit still saves
- [ ] categorization rule preview still works
- [ ] contract detection can still be opened
- [ ] transfer detection can still be opened

### Coach / Intelligence

- [ ] Coach works with no AI provider configured
- [ ] Coach works with configured AI if the instance uses it
- [ ] Intelligence admin page still opens for authorized admin
- [ ] scheduled Intelligence workers do not fail because Autopilot is unconfigured

### Banking

When banking is configured:

- [ ] provider status loads
- [ ] account sync can complete
- [ ] imported transactions remain visible

## Current rollout defaults

Deploy 5 is the first user-visible Autopilot release.

Default rollout state:

- signals = `on`
- insights = `on`
- actions = `off`
- automation-rules = `off`
- scenarios = `off`
- proactive-ai-explanations = `off`
- insight-push = `off`

An administrator can explicitly set `Autopilot__Features__insights=off` to hide the surface and
`Autopilot__Features__signals=off` to stop deterministic signal generation.

## Optional rollout override

No variable is required.

For development/test rollout only, the standard .NET configuration mapping can be used, for example:

```text
Autopilot__Features__signals=shadow
```

Allowed values:

- `off`
- `shadow`
- `on`

Invalid configured values fail startup rather than silently enabling an unknown state.

## Rollback

Deploy 0 has no schema migration and no finance-data mutation.

Rollback is therefore:

1. deploy the previous application image,
2. verify backend `/health`,
3. verify login and one finance read.

No database downgrade is required.


## Deploy 2 smoke additions

- [ ] startup applies `20260907210000_FinancialSignals`
- [ ] existing finance migration history is unchanged
- [ ] `GET /api/insights` returns an empty array for a normal user when no detector has produced signals
- [ ] a non-member receives 404 for another FullWorth Space
- [ ] dismiss/snooze/read affect only the authenticated user's signal state
- [ ] no signal is generated merely by starting the application
- [ ] AI credentials are not required

### Deploy 2 rollback

The new Intelligence tables are additive and contain no finance source-of-truth rows.

Rollback the application image without downgrading the database. The unused signal tables may remain until a later controlled cleanup.


## Deploy 3 smoke additions

Deploy 3 adds no new migration.

- [ ] `signals` resolves to `shadow` with no explicit configuration
- [ ] `insights` and every user-visible/write Autopilot feature remain `off`
- [ ] bank/import transaction commits enqueue at most one space refresh per five-minute debounce bucket
- [ ] category-only transaction changes enqueue signals without rebuilding net-worth history
- [ ] budget changes enqueue signals without rebuilding net-worth history
- [ ] daily fallback creates at most one signal job per UTC day
- [ ] signal jobs complete with no `AiInstanceSettings`, AI credential, or provider configured
- [ ] no `AiRun` row is created by deterministic signal processing
- [ ] shadow detectors can persist spending, budget, savings, data-quality, and classification-quality signals
- [ ] `/api/insights` stays hidden (404) while `insights=off`, even when shadow signals exist
- [ ] daily/space/user refresh skips inactive or tombstoned users
- [ ] no new dashboard section or navigation item is visible yet
- [ ] setting `Autopilot__Features__signals=off` makes queued signal jobs a safe no-op

### Deploy 3 rollback

Set `Autopilot__Features__signals=off` first if immediate load reduction is needed, then roll back the application image.

Existing FinancialSignal rows may remain in the additive Intelligence tables. They are not finance source-of-truth data and do not need a database downgrade.


## Deploy 4 smoke additions

Deploy 4 adds **no database migration** and keeps `signals=shadow`, `insights=off`.

- [ ] an unlinked transfer candidate can produce a shadow signal without linking either transaction
- [ ] background transfer detection only scans the recent 60-day window
- [ ] a contract price change can produce a shadow signal without changing the contract amount
- [ ] price preview does not create `PriceChangeSuggestion` rows
- [ ] unrelated newer debits on the same account do not become contract price changes
- [ ] an accepted recurring contract on account A can be detected again when recurrence moves to account B
- [ ] account A -> B recurrence produces `detector:contract-account-change` without moving the contract
- [ ] two root contract rows with continuous non-overlapping histories can produce `detector:contract-continuity`
- [ ] overlapping same-provider histories do not produce a continuity signal
- [ ] contract create/update/merge state invalidates signals without rebuilding net-worth history
- [ ] existing Contracts UI remains unchanged
- [ ] `/api/insights` remains hidden while `insights=off`
- [ ] no AI credential/provider is required and no `AiRun` is created

### Deploy 4 rollback

Set `Autopilot__Features__signals=off` to stop all Autopilot signal generation immediately, then roll back the application image.

There is no Deploy-4 schema change and no automatic transfer link, contract merge, account move, or price update to undo.


## Deploy 5 smoke additions

Deploy 5 adds **no database migration**, **no container**, and **no required environment variable**.

Default rollout state changes to:

- `signals=on`
- `insights=on`
- every write-capable Autopilot feature remains `off`

Dashboard / navigation:

- [ ] Dashboard shows a compact `Wichtig für dich` section above normal widgets
- [ ] at most 3 active insights are shown on the Dashboard
- [ ] empty insight state is calm and does not look like an error
- [ ] insight API failure does not prevent the rest of Dashboard from rendering
- [ ] Insights has no sidebar, bottom-nav, or More-menu entry
- [ ] `Alle anzeigen` opens the secondary Insights route
- [ ] browser back/forward works for the secondary route

Insight lifecycle:

- [ ] current / completed / hidden tabs load the correct API view
- [ ] mark-read persists after reload
- [ ] dismiss moves the insight to Hidden
- [ ] snooze removes the insight from Current until the selected time
- [ ] useful / irrelevant feedback persists
- [ ] resolved signals appear in Completed
- [ ] actions above change only FinancialSignalState/feedback, never finance source data

Finance object navigation:

- [ ] contract insight opens the existing contract detail
- [ ] budget insight opens the existing budget detail
- [ ] transfer/recent-transaction insight opens Transactions
- [ ] category/merchant/cashflow insight opens Analytics
- [ ] no merge, transfer-link, price-update, categorization, or other finance write is available

Presentation / privacy:

- [ ] normal insights use neutral styling
- [ ] attention uses warning styling; red is reserved for `severity=high`
- [ ] dark mode remains legible
- [ ] mobile rows and tabs have usable touch targets
- [ ] Privacy mode masks monetary values in insight summaries
- [ ] no AI badge/gradient/provider is required

PWA:

- [ ] service-worker v98 installs successfully
- [ ] `features/insights.js` is present in the static shell cache
- [ ] no `/api/insights` response is cached by the service worker

### Deploy 5 rollback

For immediate UI rollback set:

`Autopilot__Features__insights=off`

The dashboard then hides the insight surface because the API returns 404.

If deterministic generation must also stop, set:

`Autopilot__Features__signals=off`

Then roll back the image normally. There is no Deploy-5 schema change and no finance mutation to undo.


## Deploy 6 smoke additions

Deploy 6 adds **no database migration**, **no required environment variable**, and **no automatic merge execution**.

Contract duplicate insight:

- [ ] opening a `contract-pair` insight loads a merge preview
- [ ] preview names the proposed canonical/main contract
- [ ] preview lists the source contract history that would be folded into it
- [ ] preview shows combined matched-payment count and recent combined payments
- [ ] preview shows the number of payment accounts involved
- [ ] provider/cycle/amount differences appear as warnings
- [ ] monetary preview values respect Privacy mode
- [ ] there is no merge/confirm/execute button in the Insight surface

Backend safety:

- [ ] `POST /api/contracts/merge-preview` accepts at least two visible contract IDs
- [ ] same-currency contracts produce a deterministic canonical proposal
- [ ] latest matched payment wins canonical selection before stable tie-breakers
- [ ] mixed-currency preview is rejected
- [ ] preview does not write `MergedIntoContractId`
- [ ] preview does not call `SaveChanges`
- [ ] preview does not call `ContractStore.MergeForUserAsync`
- [ ] existing `/api/contract-parity/merge` behavior is unchanged

### Deploy 6 rollback

Roll back the application image. Deploy 6 has no schema change and no new persisted state.

The existing duplicate insight remains useful without the preview; no finance data needs reversal.


## Deploy 7 smoke additions

Deploy 7 enables one narrowly scoped write action: **explicitly confirmed contract merge**.

Rollout:

- [ ] `contract-merge-execution=on` by default
- [ ] generic `actions=off` remains unchanged
- [ ] `Autopilot__Features__contract-merge-execution=off` hides the merge button
- [ ] the same flag makes `POST /api/contracts/merge-execute` return 403

Confirmation flow:

- [ ] duplicate-contract insight loads the current merge preview first
- [ ] merge button is visible only when the current user has write access
- [ ] clicking merge opens the shared confirmation dialog
- [ ] execute request sends contract IDs, canonical contract ID and exact preview token
- [ ] no Insight code calls `/api/contract-parity/merge` directly
- [ ] successful merge dismisses the handled insight and refreshes the surface

State safety:

- [ ] changed contract data after preview returns 409
- [ ] changed payment history after preview changes the token and returns 409
- [ ] a source already merged elsewhere returns 409
- [ ] retrying the same successful merge returns 200 with `alreadyApplied=true`
- [ ] an idempotent retry still requires write access
- [ ] read-only members see the preview but cannot execute
- [ ] mixed currencies still cannot be merged
- [ ] existing merged source history remains accessible through the canonical contract
- [ ] existing manual contract merge endpoint behavior remains unchanged

UX:

- [ ] confirmation names the canonical/main contract and source contract(s)
- [ ] no automatic merge occurs from detection, background jobs, Coach or AI
- [ ] stale preview shows an inline refresh action instead of retrying blindly
- [ ] monetary values remain masked in Privacy mode

### Deploy 7 rollback

Immediate action rollback:

`Autopilot__Features__contract-merge-execution=off`

This leaves Insights and deterministic signals running while disabling new merge execution.

Application rollback is safe because `MergedIntoContractId`, merged-source history and unmerge support already existed before Deploy 7. Do not perform a schema rollback.
