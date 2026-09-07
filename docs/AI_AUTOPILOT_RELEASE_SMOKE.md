# AI Autopilot Release Smoke Checklist

Baseline for the deployable Autopilot rollout.

## Current migration heads

Main finance migrations:

- `20260906231500_ContractMerges`

Intelligence migrations:

- `20260906181500_CloudOperationalRegistries`

Deploy 0 adds **no database migration**.

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

The following rollout features must all be `off` unless an administrator explicitly overrides optional configuration:

- signals
- insights
- actions
- automation-rules
- scenarios
- proactive-ai-explanations
- insight-push

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
