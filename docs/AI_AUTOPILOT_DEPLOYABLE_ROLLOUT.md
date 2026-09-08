# FullWorth AI Autopilot — Deployable Step-by-Step Rollout

Completed: **Deploy 0 — Baseline and guardrails**, **Deploy 1 — FinancialContext foundation**, **Deploy 2 — FinancialSignal schema and API**, and **Deploy 3 — first deterministic detectors in shadow mode**.  
Current implementation: **Deploy 4 — transfer and contract signals in shadow mode**.

This plan turns the AI Autopilot implementation into small releases that can be deployed between every major step.

The rule for the whole project:

> main must remain deployable after every merge.

No PR may require an unfinished follow-up PR in order to start FullWorth safely.

Related:
- [AI_AUTOPILOT_PLAN.md](AI_AUTOPILOT_PLAN.md)
- [AI_AUTOPILOT_IMPLEMENTATION_PLAN.md](AI_AUTOPILOT_IMPLEMENTATION_PLAN.md)

---

# 1. Release rules

Every implementation PR must satisfy all of these before merge:

1. build succeeds
2. affected tests pass
3. migrations are forward-safe
4. old data remains valid
5. new UI is hidden if its backend is not ready
6. AI-off mode keeps working
7. existing Docker setup needs no mandatory new environment variable
8. no new container is required
9. startup migration succeeds on an existing database
10. rollback instructions are known before release

For risky features, code lands disabled first and is enabled in a later deploy.

---

# 2. Deployment flow after every step

After each release candidate:

1. merge the isolated PR to `main`
2. run the manual CI workflow
3. build the release tag
4. publish AMD64 + ARM64 images through the existing release workflow
5. deploy the new images
6. wait for backend migrations/startup to complete
7. verify `/health`
8. open the web app and perform the release-specific smoke test
9. verify logs contain no repeated worker/migration errors
10. keep the release deployed before starting the next production-visible step

For early testing, use prerelease tags such as:

`vX.Y.Z-alpha.N`

Promote to a normal stable tag only after the last smoke test for that release train.

---

# 3. Database migration policy

AI Autopilot uses expand-first migrations.

Allowed in an intermediate deploy:

- new nullable columns
- new tables
- new indexes
- additive enum/string states
- new endpoints
- code that understands both old and new data

Not allowed in the same release that introduces replacement code:

- dropping old columns
- making existing nullable data suddenly required
- renaming tables without compatibility
- deleting old API behavior still used by the currently deployed frontend

Destructive cleanup happens only after at least one successfully deployed release no longer uses the old structure.

Intelligence metadata stays in the separate Intelligence EF migration history.

---

# 4. Feature rollout policy

Use three states where needed:

- **off** — code exists but no user behavior changes
- **shadow** — calculations run and are logged/tested, but users do not see results
- **on** — visible and actionable

Do not add required environment variables for these flags.

Prefer database/admin settings with safe defaults.

Default after schema-only deploy: **off**.

---

# 5. Deploy 0 — Baseline and guardrails

## Goal

Make the current main branch the reference point before new Autopilot code lands.

## Work

- document current DB migration heads
- record current Coach/Intelligence API smoke cases
- add Autopilot feature-state constants/settings
- add a release smoke checklist
- add architecture tests ensuring:
  - signals do not require AI
  - no direct AI-to-finance writes
  - no new BFF bypass
  - no new main-nav AI item

## User-visible change

None.

## Deploy condition

CI green.

## Smoke test

- login
- dashboard
- transactions
- contracts
- budgets
- Coach local mode
- optional configured AI
- bank sync still works

## Rollback

Normal image rollback. No finance schema change.

---

# 6. Deploy 1 — FinancialContext foundation

## Goal

Introduce the shared typed financial read model without changing behavior.

## Work

Create:

- `FinancialContextSnapshot`
- `FinancialContextSnapshotService`
- completeness/period metadata
- permission-aware account scoping

Initially use it only in tests and diagnostics.

## Important

Do not replace `CoachContextBuilder` yet.

Run parity tests between new context values and current Coach/domain calculations.

## Feature state

Off/internal only.

## User-visible change

None.

## Deploy condition

- no migrations required unless cache metadata is added
- snapshot tests pass
- household/access-control tests pass

## Smoke test

Normal existing application behavior.

## Rollback

Normal image rollback.

---

# 7. Deploy 2 — FinancialSignal schema and API, no detectors

## Goal

Add the durable signal foundation safely.

## Work

Add Intelligence DB tables:

- `FinancialSignal`
- `FinancialSignalState`

Add:

- signal store
- semantic-key upsert
- resolve/expire lifecycle
- per-user visibility
- read/dismiss/snooze endpoints

Do not generate production signals yet.

## Feature state

Off.

## User-visible change

None.

## Deploy condition

- migration applies on copied production-style database
- zero signals by default
- endpoint authorization tests pass

## Smoke test

- startup migration completes
- existing Intelligence admin still works
- existing Coach suggestions still work

## Rollback

Roll application image back.

Do not automatically downgrade the database. Additive unused tables are safe to leave in place.

---

# 8. Deploy 3 — First detectors in shadow mode

## Goal

Prove signal quality without showing anything to users.

## Work

Implement deterministic:

- spending shift
- budget drift
- savings/surplus change
- classification/data-quality checks

Add job type:

- `signal-refresh-user`
- `signal-refresh-space`
- daily deterministic fallback

Important: these jobs run independently of `AiInstanceSettings.Enabled`.

## Execution

Use event-driven enqueue after relevant imports/writes.

Add debounce/idempotency.

## Feature state

Shadow.

Signals may be persisted but API/UI does not surface them normally.

## User-visible change

None.

## Deploy condition

Review real generated signals on a test/demo instance.

Check:

- volume
- false positives
- query load
- job duration
- duplicate suppression

## Smoke test

Perform a transaction import and confirm:

- finance import succeeds
- signal refresh runs afterward
- no repeated job loop
- no AI credential is required

## Rollback

Disable signal workers/settings first if necessary, then roll image back.

---

# 9. Deploy 4 — Transfer and contract signals in shadow mode

## Goal

Integrate existing high-value domain logic.

## Work

Reuse:

- `TransferDetectionService`
- `ContractDetectionService`
- price-change logic

Add signal detectors for:

- unlinked transfer candidates
- contract price changes
- probable duplicate/continuous contracts across payment-account changes

Do not merge contracts automatically.

## Feature state

Shadow.

## User-visible change

None.

## Deploy condition

Test specifically:

- same contract paid from account A then account B
- similar provider names
- same provider but genuinely separate contracts
- overlapping vs continuous histories

## Smoke test

Existing contracts page remains unchanged and usable.

## Rollback

Disable detector generation. No finance data mutated.

---

# 10. Deploy 5 — Read-only "Wichtig für dich"

## Goal

Expose useful signals without allowing financial writes.

## Work

Dashboard:

- compact `Wichtig für dich`
- maximum 3 signals
- "Alle anzeigen"

Secondary insight view:

- current
- completed
- hidden

Detail supports only:

- mark read
- dismiss
- snooze
- useful / irrelevant
- open affected existing object

No action proposal execution yet.

## Feature state

On for deterministic signals.

## User-visible change

First visible Autopilot feature.

## Design constraints

- no new bottom-nav item
- no AI gradient/badge clutter
- red only for real urgent risk
- neutral cards by default

## Deploy condition

- empty state works
- signal API failure does not break dashboard
- mobile and dark mode checked

## Smoke test

- dismiss one insight
- reload
- verify it stays hidden
- snooze one
- verify it does not reappear immediately

## Rollback

Feature flag off; dashboard returns to previous layout.

---

# 11. Deploy 6 — Contract merge preview only

## Goal

Solve the real duplicate-contract/account-switch problem safely.

## Work

Create `ContractMergeService`.

First release exposes:

- duplicate signal
- merge preview
- canonical contract proposal
- combined history preview

But execution remains disabled.

## Feature state

Preview only.

## User-visible change

User can see exactly what FullWorth would merge.

## Deploy condition

Preview proves:

- no transaction/history loss
- account switches are represented correctly
- fields selected for canonical contract are deterministic

## Smoke test

Open a duplicate signal and inspect merge preview.

## Rollback

No finance mutation exists yet.

---

# 12. Deploy 7 — Contract merge execution

## Goal

Turn verified merge previews into real actions.

## Work

Enable explicit confirm:

- preserve canonical contract
- set `MergedIntoContractId` for duplicates
- retain historical rows
- audit
- idempotency

No automatic merge.

## Feature state

On after confirmation.

## Deploy condition

Integration tests cover:

- retry same merge
- refresh during merge
- user lacks capability
- already-merged contract
- changed contract state after preview

## Smoke test

On demo/test data:

1. create duplicate contract state
2. open insight
3. preview
4. confirm
5. verify only one logical active contract remains
6. verify old history remains accessible

## Rollback

Do not attempt schema rollback.

Application rollback must continue to understand existing `MergedIntoContractId`, which is already part of current contract handling.

---

# 13. Deploy 8 — Generic ActionProposal foundation

## Goal

Establish one safe write path for future Coach/Insight actions.

## Work

Add:

- `ActionProposal` table
- handler registry
- validate
- preview
- execute
- reject
- optional undo
- idempotency
- audit

First handlers:

- transaction category change
- categorization-rule create/update
- transfer link

Keep contract merge adapter on the same framework after parity is proven.

## Feature state

On only for explicit user-confirmed actions.

## User-visible change

Insight/Coach can show a structured preview card instead of just telling the user what to do.

## Deploy condition

No model/provider can specify arbitrary handler names or arbitrary endpoints.

## Smoke test

Create category-change proposal, confirm it, verify transaction and audit.

## Rollback

Disable proposal creation/execution. Existing executed finance changes remain normal finance data.

---

# 14. Deploy 9 — Natural-language categorization rules

## Goal

Let users describe supported rules normally.

## Work

Add `NaturalLanguageRuleCompiler`.

Supported first:

- merchant/text match
- amount min/max
- direction
- category
- transfer flag

Compile into existing `RuleWrite`.

Then always run existing deterministic rule preview.

## Important

AI output never becomes an active rule directly.

Flow:

1. text
2. structured draft
3. validation
4. historical preview
5. user confirmation
6. existing rule store

## Feature state

On only when AI provider is available for free-form parsing.

Existing manual rule builder remains fully functional without AI.

## User-visible change

Add `Regel beschreiben` to Rules.

## Unsupported semantics

If an exception cannot be represented by the current rule engine, say so.

Do not approximate silently.

## Smoke test

"Amazon unter 20 € zu Haushalt"

Verify generated structured rule and historical matches before saving.

## Rollback

Hide text compiler; manually created structured rules continue working.

---

# 15. Deploy 10 — AutomationRule schema, disabled

## Goal

Prepare non-categorization rules without exposing incomplete behavior.

## Work

Add limited `AutomationRule` DSL:

Triggers:

- transaction changed
- balance changed
- daily/month progress
- contract changed

Conditions:

- category spend threshold
- merchant spend threshold
- account balance threshold
- budget percentage
- cash-buffer threshold

Actions initially only:

- create/update FinancialSignal

## Feature state

Off/shadow.

## User-visible change

None.

## Deploy condition

Historical/dry-run tests only.

## Rollback

Leave additive table unused.

---

# 16. Deploy 11 — User-created alert rules

## Goal

Enable safe natural-language financial monitoring.

## Examples

- "Warn mich wenn Essen gehen diesen Monat über 250 € geht."
- "Sag mir wenn mein Girokonto unter 500 € fällt."

## Work

Compile into visible `AutomationRule`.

Show:

- trigger
- condition
- signal action
- dry-run result

User confirms activation.

## Feature state

On.

## User-visible change

Rules view now supports categorization and alert rules without presenting them as opaque AI automations.

## Smoke test

Create threshold rule, trigger it with test data, verify exactly one deduplicated insight appears.

## Rollback

Disable automation-rule evaluator. Rules remain stored but inactive.

---

# 17. Deploy 12 — Simulation engine, API first

## Goal

Land deterministic scenario calculations independently from UI/AI parsing.

## Work

Implement pure calculations for:

- affordability
- savings increase
- recurring-cost change
- income reduction
- wealth target
- contract cancellation

## Feature state

Internal/API only.

## User-visible change

None.

## Deploy condition

Golden math tests and explicit assumption tests pass.

No AI call involved.

## Smoke test

Run API scenario examples and verify deterministic repeatability.

## Rollback

Normal image rollback.

---

# 18. Deploy 13 — Simulation UI and Coach integration

## Goal

Make scenarios useful to normal users.

## Work

Coach structured scenario card:

- assumptions
- current state
- simulated state
- difference

Add a minimal simulation entry where useful, but do not create another primary nav item.

AI may parse a natural-language scenario into typed inputs when configured.

Without AI, explicit form/known Coach intents still work.

## User-visible change

Questions such as:

- "Kann ich mir 800 € leisten?"
- "Was wenn ich 200 € mehr spare?"

return structured deterministic results.

## Smoke test

Run same scenario with AI on and AI off; numbers must match.

## Rollback

Disable new presentation/parser; deterministic engine remains harmless.

---

# 19. Deploy 14 — AI explanations in opt-in/background-off mode

## Goal

Improve wording without changing decisions.

## Work

Add:

- `SignalExplanationService`
- explanation cache
- explicit provider data-boundary metadata
- minimized per-signal provider input

Default:

- AI Coach remains as configured
- proactive background signal explanation is off

User can request explanation manually.

## User-visible change

"Erklären" can give a more natural explanation for a deterministic signal.

## Deploy condition

Compare deterministic facts against generated text.

No generated value may override stored impact/severity/evidence.

## Smoke test

Disable provider during request. Insight must still show deterministic fallback text.

## Rollback

Turn explanation setting off.

---

# 20. Deploy 15 — Proactive AI explanation opt-in

## Goal

Allow selected users to have background-generated signal summaries.

## Work

Add setting:

- proactive AI explanations

Only explain already-ranked signals likely to be shown.

Do not explain every generated signal.

Cache by signal version.

## User-visible change

More natural signal wording.

## Cost protection

Use current AI budget guard.

Never run if budget disallows it.

## Smoke test

Verify one signal version produces at most one cached explanation per locale/provider/model.

## Rollback

Disable proactive explanation setting.

---

# 21. Deploy 16 — Financial insight push

## Goal

Push only genuinely important signals.

## Work

Add notification type:

- `financial_insight`

Push policy:

- high enough severity
- sufficient confidence
- high rank
- not snoozed/dismissed
- semantic version not already pushed

Do not push every insight.

## User-visible change

Optional high-value proactive alerts.

## Smoke test

Trigger one urgent signal twice; verify one notification.

## Rollback

Disable notification type/policy without disabling in-app insights.

---

# 22. Deploy 17 — Coach unification

## Goal

Stop Coach becoming a parallel intelligence stack.

## Work

Incrementally move Coach reads to:

- `FinancialContextSnapshot`
- current FinancialSignals
- Scenario Engine
- ActionProposal framework

Keep old aggregation until parity tests pass.

Migrate one domain at a time.

## Feature state

Progressively enabled internally.

## Deploy strategy

This is intentionally multiple small PRs/deploys:

### 17A
Coach reads current signals.

### 17B
Coach scenarios use Scenario Engine.

### 17C
Coach writes use ActionProposal.

### 17D
Coach context migrates to FinancialContextSnapshot.

### 17E
Remove duplicated legacy calculations only after one successful deployed release using the new path.

## Rollback

Each substep retains previous code until the following deploy confirms parity.

---

# 23. Deploy 18 — Hardening release

## Goal

Finish the first Autopilot production release.

## Work

- performance profiling
- indexes based on real query plans
- signal retention cleanup
- stale proposal cleanup
- action audit review
- accessibility
- mobile UX
- dark mode
- push rate limits
- AI budget/cost dashboard
- false-positive review
- security regression suite
- backup/restore test including Intelligence tables

## Stable release condition

Do not call Autopilot stable until:

- AI-off path works end-to-end
- account-switch contract case works
- no automatic destructive action exists
- action confirmations are permission-revalidated
- signal volume is controlled
- background jobs do not cause load spikes
- backup/restore includes new data
- AMD64 and ARM64 release images pass smoke tests

---

# 24. Per-PR checklist

Every Autopilot PR description should contain:

```text
Deployment state:
[ ] invisible/internal
[ ] shadow
[ ] visible

Database:
[ ] no migration
[ ] additive migration
[ ] destructive migration (must not be used in normal rollout)

AI dependency:
[ ] none
[ ] optional
[ ] required only for this isolated feature

Rollback:
...

Smoke test:
...

Feature switch:
...
```

---

# 25. What gets deployed first

The first production implementation sequence is therefore:

1. Deploy 0 — guardrails
2. Deploy 1 — FinancialContext
3. Deploy 2 — signal DB/API
4. Deploy 3 — base detectors shadow
5. Deploy 4 — transfer/contract detectors shadow
6. Deploy 5 — read-only insights
7. Deploy 6 — contract merge preview
8. Deploy 7 — contract merge confirm
9. Deploy 8 — generic safe actions
10. Deploy 9 — natural-language categorization rules
11. Deploy 10 — automation schema shadow
12. Deploy 11 — alert rules
13. Deploy 12 — simulation backend
14. Deploy 13 — simulation UX/Coach
15. Deploy 14 — on-demand AI explanations
16. Deploy 15 — proactive AI explanations
17. Deploy 16 — insight push
18. Deploy 17A-E — Coach unification
19. Deploy 18 — hardening/stable

At every numbered point above the current `main` branch is expected to be safe to release and run indefinitely if development stops there.
