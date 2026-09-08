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
- [ ] Autopilot rollout defaults to `off`

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

## Deploy 0 expected behavior

No user-visible Autopilot feature exists yet.

Deploy 3 default rollout state:

- signals = `shadow`
- insights = `off`
- actions = `off`
- automation-rules = `off`
- scenarios = `off`
- proactive-ai-explanations = `off`
- insight-push = `off`

An administrator can still explicitly override `signals=off` to disable shadow processing.

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
