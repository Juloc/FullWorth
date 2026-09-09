# FullWorth AI Autopilot — Implementation Plan

Status: implementation plan  

Current behaviour and the deployment levers: [AI_AUTOPILOT.md](AI_AUTOPILOT.md)

Depends on: `docs/AI_AUTOPILOT.md`  
Phase A (Coach credibility): already implemented.

## 1. Goal

FullWorth should become useful without requiring the user to open Coach.

The product should:

- detect relevant financial changes automatically,
- explain why they matter,
- keep categorization/contracts/transfers cleaner,
- propose concrete next actions,
- execute only after explicit confirmation,
- let users create rules in natural language,
- simulate financial decisions deterministically,
- keep the full deterministic experience working when AI is disabled.

AI is an optional interpretation and language layer. It must not become the source of financial truth.

## 2. Existing components to reuse

Do not build parallel systems for capabilities FullWorth already has.

### Existing finance/domain logic

Reuse:

- `CoachContextBuilder` and deterministic Coach calculations
- `TransactionRuleEngine`
- `CategoryStore.PreviewRuleForUserAsync`
- `CategoryStore.UpsertRuleForUserAsync`
- `TransferDetectionService`
- `ContractDetectionService`
- contract candidate dismissal / feedback
- budget projection/status logic
- analytics services
- wealth overview / net-worth snapshot services
- price-change services
- notification dispatcher + push preferences
- `AuditService`

### Existing intelligence infrastructure

Reuse:

- `IntelligenceDbContext`
- `IntelligenceJob`, leases and watermarks
- `IntelligenceSuggestion`
- `IntelligenceFeedbackEvent`
- `AiRun` / `AiRunItem`
- provider registry
- cost/budget guards
- Cloud Intelligence consent/outbox architecture
- scheduled intelligence workers

### Important separation

`IntelligenceSuggestion` remains for AI-originated mapping/enrichment proposals.

A new `FinancialSignal` model represents deterministic or AI-assisted financial observations shown to the user.

Do not force every insight into `IntelligenceSuggestion`.

---

# 3. Target architecture

```text
Finance source-of-truth
(accounts / transactions / contracts / budgets / wealth / purchases)
        |
        v
FinancialContextSnapshot
        |
        +------------------------+
        |                        |
        v                        v
Deterministic Signal        Scenario Engine
Detectors                   (pure calculations)
        |
        v
FinancialSignal Store
        |
        +----------+-------------+-------------+
        |          |                           |
        v          v                           v
Insight UI     Coach context            Notification policy
        |
        v
Optional AI Explanation
        |
        v
ActionProposal
        |
        v
Preview -> Confirm -> Domain handler -> Audit
```

Rules from natural language use the same domain handlers and existing rule engine.

---

# 4. Canonical personal financial context

## 4.1 Problem

Coach already builds a useful cross-domain context, but signal detection and simulations must not each re-query and reinterpret the same data differently.

## 4.2 New service

Create:

`Modules/Intelligence/Context/FinancialContextSnapshotService.cs`

Models:

```csharp
FinancialContextSnapshot
- UserId
- FullWorthSpaceId
- BaseCurrency
- AsOf
- Completeness
- Accounts
- CashFlow
- Categories
- Merchants
- Contracts
- Budgets
- Wealth
- Debts
- Portfolios
- RecentTransactions
- SpendingReviews
- DataQuality
```

The snapshot is a typed read model, not a second source of truth.

### Rules

- Build from existing stores/services.
- Respect the requesting user's accessible accounts and capabilities.
- Do not query finance tables directly from AI providers.
- No raw secret/payment credentials.
- Avoid IBAN/account identifiers unless a deterministic local detector explicitly requires them.
- Keep completeness flags for every aggregate.
- Every derived value must carry its period/as-of date.

## 4.3 Coach migration

Do not rewrite Coach immediately.

Sequence:

1. introduce the snapshot service,
2. add parity tests against existing `CoachContextBuilder`,
3. migrate Coach calculations incrementally,
4. delete duplicated aggregation only after output parity is proven.

---

# 5. Financial Signal foundation

## 5.1 New persistence model

Store in `IntelligenceDbContext`.

### FinancialSignal

```text
Id
FullWorthSpaceId
UserId
Type
SubjectType
SubjectId
SemanticKey
Source
Severity
Confidence
ImpactAmount
ImpactCurrency
TitleKey
PayloadJson
EvidenceJson
RankScore
DetectedAt
UpdatedAt
ValidUntil
ResolvedAt
Version
```

### Signal state

Keep user interaction separate:

```text
FinancialSignalState
SignalId
UserId
State          unread | read | dismissed | snoozed
SnoozedUntil
Feedback       useful | irrelevant | null
UpdatedAt
```

Unique: `SignalId + UserId`.

Initially signals are user-scoped even inside a shared FullWorth Space. This avoids leaking information from accounts a household member cannot access.

## 5.2 Identity and deduplication

`SemanticKey` must identify the same logical observation over time.

Examples:

- `budget-drift:{budgetId}:{yyyy-MM}`
- `merchant-spike:{merchantKey}:{yyyy-MM}`
- `contract-duplicate:{canonicalProviderKey}`
- `transfer-candidate:{txA}:{txB}`
- `cash-buffer:{accountSet}:{yyyy-MM-dd}`

Upsert the signal instead of creating new rows every scan.

## 5.3 Signal lifecycle

- active signal gets updated while condition remains true
- resolved when the condition disappears
- dismissed stays suppressed until a meaningful version/change occurs
- snoozed remains hidden until `SnoozedUntil`
- stale signals expire automatically
- never resurrect the exact same dismissed condition without a meaningful change

---

# 6. Deterministic detectors

Create:

`Modules/Intelligence/Signals/IFinancialSignalDetector.cs`

Each detector:

```csharp
Task<IReadOnlyList<DetectedFinancialSignal>> DetectAsync(
    FinancialContextSnapshot context,
    SignalDetectionScope scope,
    CancellationToken ct)
```

AI must not be required for these detectors.

## 6.1 First detector set

### A. Spending shift

Detect meaningful changes in:

- total outgoing
- category spending
- merchant spending

Avoid false positives:

- compare equal elapsed periods for current partial month
- use prior 3 comparable periods where possible
- require both absolute and percentage change
- suppress tiny values

Initial threshold:

- absolute change >= 20 EUR equivalent
- percentage change >= 25%
- minimum historical baseline >= 20 EUR

Threshold becomes configurable later.

### B. Budget drift

Reuse existing budget projections.

Signals:

- likely to exceed budget
- already over budget
- large early-month consumption

Do not duplicate existing budget notification calculations.

### C. Savings-rate / surplus change

Compare:

- latest 90-day average monthly surplus
- prior 90-day period

Only produce when FX/data completeness is sufficient.

### D. Cash-buffer pressure

Initial deterministic version:

- visible liquid balances
- known upcoming contracts
- observed recurring income
- active budgets

Produce a warning only when the projected buffer crosses a defined threshold.

No claim that FullWorth can predict every future transaction.

### E. Contract duplicate / overlap

This is important for account switches.

Reuse contract identity normalization and recurring payment history.

A logical contract must not become multiple contracts only because its payment account changed.

Detection factors:

- normalized provider identity
- currency
- similar billing cycle
- similar amount
- non-overlapping or continuous payment history
- source-account switch
- existing `MergedIntoContractId`

Output:

- probable duplicate contracts
- one preferred canonical contract
- previewable merge action

### F. Contract price change

Reuse existing price-change logic.

Signal:

- amount increased/decreased
- annualized financial effect
- effective date

### G. Transfer candidates

Reuse `TransferDetectionService`.

Expose medium/high confidence unlinked pairs as signals.

Do not invent a second transfer matcher.

### H. Classification quality

Detect:

- uncategorized recurring merchants
- repeated manual category corrections
- category that conflicts with a confirmed learned merchant mapping
- repeated user corrections that justify a rule suggestion

### I. Data-quality signal

Examples:

- stale bank sync
- incomplete historical FX data
- important account without current balance
- large share of uncategorized transactions
- missing recurring-contract information

Data-quality signals should not be mixed with "you spent too much" warnings.

### J. Net-worth explanation

When net worth changes materially:

- decompose change into accounts, investments, assets, liabilities
- show known contributions
- mark unexplained remainder if data is incomplete

No AI is required for the decomposition.

---

# 7. Signal execution model

## 7.1 Event driven first

Do not add another minute-level global scanner.

Enqueue signal refresh after meaningful writes/imports:

- bank sync completion
- transaction import
- transaction category change
- rule reapply
- transfer link/unlink
- contract accept/update/merge
- budget update
- wealth valuation update
- portfolio import
- account balance update

## 7.2 Reuse IntelligenceJob infrastructure

Extend the intelligence job router with deterministic job types:

- `signal-refresh-user`
- `signal-refresh-space`
- `signal-daily-fallback`

These jobs must run even when AI is disabled.

The current scheduled AI processor checks `AiInstanceSettings.Enabled`; signal jobs must be dispatched before that AI gate.

## 7.3 Debounce

Multiple imports should collapse into one refresh.

Example idempotency key:

`signals:{spaceId}:{userId}:{5-minute-bucket}`

## 7.4 Fallback

One daily deterministic fallback scan catches missed events.

No AI credential required.

---

# 8. Ranking and relevance

Signals are not a chronological notification feed.

Calculate deterministic `RankScore` from:

- severity
- financial impact
- confidence
- recency
- actionability
- novelty
- prior user feedback

Penalize:

- repeated low-value signals
- signals the user repeatedly dismisses
- low confidence
- tiny financial impact

Hard limits:

- dashboard: maximum 3 active signals
- push: only high-priority signals
- no more than one push for the same semantic condition/version

---

# 9. User-facing Insight UX

## 9.1 No new primary navigation item

Mobile navigation is already dense.

Do not add "AI" or "Insights" as another bottom-nav item.

## 9.2 Dashboard

Add a compact section:

**Wichtig für dich**

Maximum 3 items.

Each item:

```text
Short title
One-sentence reason
Financial impact / relevant number
Primary action
More menu
```

Example:

```text
Stromkosten gestiegen
+18 € pro Monat gegenüber deinem bisherigen Niveau.
[Details]
```

## 9.3 Full insight list

Open as a secondary route/view, reachable from:

- "Alle anzeigen" on dashboard
- Coach
- notification deep link

Not a main navigation item.

Filters:

- Aktuell
- Erledigt
- Ausgeblendet

Do not expose internal detector names.

## 9.4 Detail

Detail shows:

- what changed
- why FullWorth thinks it matters
- evidence
- confidence only when useful
- affected objects
- proposed action
- dismiss
- snooze
- "FullWorth beibringen" where applicable

## 9.5 Styling

- neutral default
- amber only for meaningful attention
- red only for actual urgent/risk conditions
- no AI-gradient styling
- no decorative "AI" badges everywhere

---

# 10. Optional AI explanation layer

## 10.1 Service

Create:

`SignalExplanationService`

Input:

- one signal
- minimal evidence bundle
- locale
- user-visible context only

Output:

```json
{
  "headline": "...",
  "summary": "...",
  "whyItMatters": "...",
  "suggestedNextStep": "..."
}
```

## 10.2 AI does not decide

AI must not set:

- financial amount
- severity
- signal type
- confidence of deterministic detector
- action payload
- whether a transaction really happened

It may explain and reorder already validated options.

## 10.3 Fallback

Every signal has deterministic template text.

If AI is off/fails/budget is exhausted:

- insight still works
- action still works
- Coach still works locally

## 10.4 Data minimization

Provider input should contain only fields needed for that signal.

Examples:

Merchant spending signal:

- merchant display name
- current amount
- historical baseline
- period
- category

Not:

- all raw transactions
- account identifiers
- unrelated contracts
- household profile

## 10.5 Caching

Cache generated explanation by:

- signal id
- signal version
- locale
- provider/model

Do not pay again if evidence has not changed.

---

# 11. Notifications

Reuse `NotificationDispatcher`.

Add one user-facing notification type initially:

`financial_insight`

Do not create 20 new notification toggles immediately.

## Rules

Push only if:

- signal severity is high enough
- confidence is sufficient
- user has not dismissed/snoozed it
- semantic/version dedup has not fired
- push type is enabled

Medium/low signals stay inside FullWorth.

Notification deep-links to the signal detail.

Later split categories only if users need separate controls.

---

# 12. Safe ActionProposal system

AI and detectors must never call finance write services directly.

## 12.1 Models

Store in `IntelligenceDbContext`.

### ActionProposal

```text
Id
FullWorthSpaceId
UserId
SourceSignalId
SourceConversationId
Type
SubjectType
SubjectId
PayloadJson
PreviewJson
RequiredCapability
IdempotencyKey
Status
CreatedAt
ExpiresAt
ExecutedAt
FailureCode
UndoPayloadJson
```

Statuses:

- draft
- ready
- executing
- executed
- rejected
- expired
- failed
- undone

## 12.2 Handler contract

```csharp
IActionProposalHandler
- Type
- ValidateAsync(...)
- PreviewAsync(...)
- ExecuteAsync(...)
- UndoAsync(...) // optional
```

Every execute:

1. reload current finance state
2. revalidate permissions
3. revalidate proposal assumptions
4. show preview if state changed
5. execute domain service
6. write finance audit
7. write intelligence audit
8. mark proposal executed

## 12.3 First action handlers

Implement in this order:

1. `transaction.set-category`
2. `categorization-rule.create`
3. `categorization-rule.update`
4. `transfer.link`
5. `contract.merge`
6. `budget.update`
7. `signal.snooze` / `signal.dismiss`

Money movement is explicitly not in the first implementation.

If FullWorth later gets payment-initiation APIs, money movement gets a separate higher-risk confirmation flow.

---

# 13. Contract merge action

Because duplicate contracts are a real user problem, this gets a proper domain service.

Create:

`ContractMergeService`

Preview must show:

- contracts being merged
- canonical provider/name
- combined payment history
- account history
- billing cycle
- current amount
- next due date
- fields that will win

Execution:

- preserve one canonical contract
- link duplicates through `MergedIntoContractId`
- do not delete historical contracts
- preserve transaction/payment references
- audit merge
- idempotent if retried

If account changed, canonical contract can remain account-agnostic as the existing detection logic already supports.

---

# 14. Natural-language rules

## 14.1 Principle

Natural language is a compiler input, not the stored rule format.

Never persist "a prompt that runs later".

Persist structured rules.

## 14.2 Rule compiler

Create:

`NaturalLanguageRuleCompiler`

Pipeline:

1. classify requested rule domain
2. extract structured conditions/actions
3. validate against supported schema
4. resolve categories/accounts by ID
5. run deterministic preview
6. show human-readable rule
7. user confirms
8. save structured rule

## 14.3 Categorization rules

For requests such as:

"Amazon unter 20 € meistens Haushalt"

compile directly to existing `RuleWrite`.

Use existing:

- `TransactionRuleEngine`
- `PreviewRuleForUserAsync`
- `UpsertRuleForUserAsync`

Do not create a second categorization-rule engine.

## 14.4 Exceptions

Current `CategorizationRule` cannot express arbitrary NOT/exceptions.

For:

"Amazon unter 20 € Haushalt, außer Kindle"

do not fake support.

Phase 1 options:

- create two ordered rules if semantics can be represented safely
- otherwise show: exception is not representable yet

Phase 2 may extend the rule schema with explicit condition groups.

No opaque AI workaround.

## 14.5 General automation rules

Some natural-language rules are not categorization rules:

"Warn mich wenn Essen gehen über 250 € liegt."

Create a separate limited `AutomationRule` DSL.

Initial supported trigger/action set:

Triggers:

- transaction-created
- transaction-updated
- month-progress
- balance-updated
- contract-updated
- daily

Conditions:

- category spend threshold
- merchant spend threshold
- account balance threshold
- budget percentage threshold
- recurring cost threshold
- cash-buffer threshold

Actions:

- create FinancialSignal
- optionally push if signal qualifies

No arbitrary code.
No arbitrary HTTP calls.
No money movement.

## 14.6 Rule UX

In Rules view add:

- existing manual builder
- text entry: "Regel beschreiben"

Generated preview shows:

- Wenn …
- Dann …
- matched historical count
- sample matches
- side effects
- enable toggle

User confirms before save.

---

# 15. Deterministic simulation engine

Create:

`Modules/Intelligence/Simulation/FinancialScenarioEngine.cs`

Pure calculation code where possible.

## 15.1 Scenario types

First release:

### Purchase affordability

Input:

- one-time price
- optional date

Output:

- liquid buffer after purchase
- months of typical surplus consumed
- budget conflicts
- known upcoming commitments

### Recurring cost change

"What if rent/insurance/car costs 150 € more per month?"

Output:

- monthly surplus delta
- annual effect
- target-date impact

### Savings increase

"What if I save 200 € more per month?"

Output:

- new surplus
- target date comparison
- accumulated difference

### Income reduction

"What happens if income drops by 20% for 6 months?"

Output:

- monthly deficit/surplus
- estimated liquid-buffer trajectory
- recovery point

### Wealth target

Reuse deterministic target projection:

- current net worth
- monthly contribution
- optional return assumption

### Contract change

"What if I cancel this 19.99 €/month contract?"

Output:

- monthly and annual delta
- savings-target impact

## 15.2 Assumptions

Every result must show assumptions explicitly.

Never silently assume investment return or withdrawal rate.

## 15.3 AI role

AI may:

- parse natural language into a typed scenario input
- explain the result

AI must not perform the actual calculation.

## 15.4 Persistence

Do not persist scenarios by default.

Later feature:

- Save scenario
- Compare scenarios

Only after core flow is stable.

---

# 16. Coach integration

Coach becomes the conversational surface over the same systems.

## 16.1 Read

Coach can query:

- current FinancialSignals
- FinancialContextSnapshot
- scenario engine
- existing finance facts

## 16.2 Actions

When the user says:

"Mach daraus eine Regel"

Coach creates an `ActionProposal`.

It does not execute the write directly.

## 16.3 Response rendering

Add structured response blocks:

- insight card
- scenario result
- action preview
- rule preview

Do not render everything as prose.

## 16.4 Examples

User:

"Warum war dieser Monat teurer?"

Flow:

1. deterministic signal/context decomposition
2. AI optional explanation
3. links to top categories/merchants
4. optional "Sparpotenzial ansehen"

User:

"Mach Amazon unter 20 € zu Haushalt"

Flow:

1. compile to `RuleWrite`
2. preview against history
3. show affected count/sample
4. confirm
5. create rule
6. reapply only if user explicitly chooses

---

# 17. "Teach FullWorth" feedback

Signal detail can expose contextual learning actions.

Examples:

- "Diese Kategorie ist richtig"
- "Das ist ein interner Transfer"
- "Diese Verträge gehören zusammen"
- "Diesen Hinweis nicht mehr für diesen Händler zeigen"

Write feedback through existing `IntelligenceFeedbackRecorder` where applicable.

Feedback updates:

- local learned mappings
- signal suppression
- rule suggestions
- Cloud-eligible minimized learning only when Cloud consent allows it

Do not send personal signal history to FullWorth Cloud.

---

# 18. AI settings / privacy UX

Keep the admin intelligence page for provider configuration.

User-facing product should work in three states:

### AI off

- all deterministic signals
- local explanations
- simulations
- action proposals
- manual rules
- no provider calls

### AI on, background AI off

- AI in Coach on request
- no proactive AI explanation calls

### AI on, background AI on

- selected signal explanations
- natural-language rule compilation
- optional proactive enrichment

Add user settings later:

- AI assistance
- proactive AI explanations
- external AI provider usage

Do not hide whether an external provider is used.

Provider descriptors should expose a configured data boundary:

- `local` — self-hosted/local compatible endpoint
- `external` — data leaves the FullWorth instance

Do not infer this from hostname at runtime; make it an explicit administrator configuration/property.

---

# 19. Security requirements

## Authorization

Every signal/action/scenario request is scoped by:

- authenticated user
- FullWorth Space membership
- existing domain capabilities
- accessible account set

Never trust `UserId` from browser payload.

## Prompt injection

All finance strings are untrusted data.

Generalize the existing scheduled AI pattern:

- explicit system instruction
- bounded input schema
- bounded output schema
- no external tools unless that feature explicitly requires them
- validate every referenced ID against the supplied candidate set

## Action safety

- no model-generated SQL
- no arbitrary endpoint names
- allowlisted action types only
- execute through typed domain handlers
- revalidate on execution
- idempotency required

## Cloud

- FinancialSignal rows remain local
- ActionProposal rows remain local
- raw personal context never enters Cloud learning outbox
- existing Cloud consent/minimization remains the only contribution path

---

# 20. Database migrations

Use Intelligence migration history for:

1. FinancialSignal
2. FinancialSignalState
3. SignalExplanationCache
4. ActionProposal
5. AutomationRule

Do not put these models in the main finance schema unless they become financial source-of-truth objects.

Domain-specific merge changes such as a new contract relation belong in the main finance migrations.

---

# 21. API shape

## Signals

```text
GET    /api/insights
GET    /api/insights/{id}
POST   /api/insights/{id}/read
POST   /api/insights/{id}/dismiss
POST   /api/insights/{id}/snooze
POST   /api/insights/{id}/feedback
```

## Actions

```text
POST   /api/action-proposals
GET    /api/action-proposals/{id}
POST   /api/action-proposals/{id}/refresh-preview
POST   /api/action-proposals/{id}/execute
POST   /api/action-proposals/{id}/reject
POST   /api/action-proposals/{id}/undo
```

## Natural-language rules

```text
POST   /api/rules/compile
POST   /api/rules/compile/{id}/preview
POST   /api/rules/compile/{id}/confirm
```

The confirm route delegates to existing categorization rule APIs or the AutomationRule store.

## Simulations

```text
POST   /api/scenarios/affordability
POST   /api/scenarios/cashflow
POST   /api/scenarios/goal
POST   /api/scenarios/contract-change
```

Later consolidate to one typed endpoint only if it improves client code.

---

# 22. Frontend structure

New feature modules:

```text
wwwroot/features/insights.js
wwwroot/features/insights.css
wwwroot/features/action-proposals.js
wwwroot/features/scenarios.js
wwwroot/features/rule-compiler.js
```

Shared primitives belong in existing UI modules.

Do not add:

- MutationObserver patch layers
- direct BFF calls
- feature-local dialog factories
- new random button variants

Follow existing frontend architecture guards.

---

# 23. Test plan

## Unit

- every detector threshold/edge case
- semantic dedup keys
- rank scoring
- signal lifecycle
- scenario math
- rule compiler validation
- action handler validation
- contract merge identity

## Integration

- partial-access household users cannot see another member's signals
- signal refresh works with AI disabled
- AI provider failure does not break signal generation
- action proposal permission revalidation
- stale proposal cannot execute silently
- idempotent execute
- dismissed/snoozed signal behavior
- notification dedup
- categorization rule preview parity
- contract account-switch dedup/merge

## Security

- browser cannot choose another UserId
- model cannot reference IDs not present in bounded input
- no prompt content becomes executable action type
- no action bypasses capabilities
- Cloud opt-out means no signal/action payload leaves instance

## UI baseline

- no new primary mobile nav item
- maximum 3 dashboard insights
- empty/loading/error states
- dark/light theme
- mobile signal detail
- action confirmation preview
- AI-off mode has no dead controls

---

# 24. Implementation sequence

## PR 1 — Context + signal schema

Files/modules:

- FinancialContextSnapshot models/service
- FinancialSignal models/configuration
- Intelligence migration
- signal store
- authorization-scoped signal endpoints
- lifecycle/dedup tests

No UI yet.

**Done when:** a deterministic test signal can be stored, updated, resolved and queried safely per user.

## PR 2 — Core deterministic detectors

Implement:

- spending shift
- budget drift
- savings change
- classification quality
- data quality
- transfer candidates

Wire event-driven refresh + daily fallback.

**Done when:** useful signals appear with AI completely disabled.

## PR 3 — Contract intelligence

Implement:

- duplicate contract detector
- account-switch continuity
- price-change signal
- ContractMergeService
- merge preview tests

**Done when:** changing the payment account does not create separate logical contracts without a merge path.

## PR 4 — Insight UX

Implement:

- dashboard "Wichtig für dich"
- secondary insight list
- insight detail
- read/dismiss/snooze/useful/irrelevant

No new bottom nav item.

**Done when:** user can understand and manage signals without Coach.

## PR 5 — ActionProposal framework

Implement:

- models/migration
- handler registry
- preview/execute/reject/undo APIs
- transaction category, categorization rule and transfer handlers
- contract merge handler
- budget handler
- Coach/action UI blocks

**Done when:** every write from an insight/Coach is previewed and explicitly confirmed.

## PR 6 — Natural-language rules

Implement:

- compiler
- category/account resolver
- existing `RuleWrite` target
- historical preview
- structured confirmation UI
- limited AutomationRule DSL

**Done when:** simple supported phrases compile to visible structured rules; unsupported semantics are rejected clearly.

## PR 7 — Simulation engine

Implement:

- affordability
- recurring-cost change
- income change
- savings change
- target scenario
- contract cancellation scenario
- Coach structured scenario rendering

**Done when:** all numbers come from deterministic code and assumptions are shown.

## PR 8 — Optional AI explanations + push

Implement:

- SignalExplanationService
- explanation cache
- minimal provider payloads
- `financial_insight` notification type
- high-priority push policy

**Done when:** AI improves wording but disabling it changes no core signal behavior.

## PR 9 — Coach + product unification

Refactor:

- Coach context toward FinancialContextSnapshot
- signal-aware starters
- "why did this change?" answers use signal evidence
- rule/action/scenario cards
- remove duplicated calculations after parity tests

**Done when:** Coach is a conversational view of the same intelligence system rather than a parallel implementation.

## PR 10 — Hardening / telemetry

Add:

- signal acceptance/dismiss metrics
- action completion metrics
- detector false-positive review
- AI cost attribution by feature
- DB cleanup/retention
- accessibility
- performance profiling
- documentation

---

# 25. Priority order

Do not start with AI prose.

Priority:

1. deterministic signal correctness
2. contract duplicate/account-switch correctness
3. signal UX
4. safe action proposals
5. rule compilation
6. simulations
7. AI explanations
8. additional proactive AI

This order gives FullWorth value even for users who never configure AI.

---

# 26. Explicit non-goals for the first implementation

Do not build yet:

- autonomous bank transfers
- autonomous contract cancellation
- unrestricted web agents
- arbitrary user-written scripts
- arbitrary AI tool execution
- investment buy/sell execution
- tax filing submission
- constant minute-level scanning
- another standalone "AI dashboard"

---

# 27. Final product behavior

The target experience:

1. FullWorth imports/syncs data.
2. Deterministic detectors identify meaningful changes.
3. User sees at most a few relevant signals.
4. AI optionally explains those signals better.
5. User can ask Coach for context.
6. FullWorth can prepare a rule, correction, merge or budget change.
7. User sees exactly what will change.
8. User confirms.
9. Typed domain code executes it.
10. FullWorth learns from the decision.

The user should not need to know which internal parts are AI and which are deterministic unless it matters for privacy, cost or reliability.
