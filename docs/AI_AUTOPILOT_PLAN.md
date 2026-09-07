# FullWorth AI Autopilot Plan

## Product principle

AI is not a separate chatbot feature. It is an intelligence layer over FullWorth.

The default product behavior should be:

1. deterministic finance logic first,
2. AI only where interpretation adds value,
3. suggestions before actions,
4. explicit confirmation before money-moving or destructive actions,
5. clear evidence for every important recommendation.

The goal is not "chat with your transactions". The goal is that FullWorth keeps a user's financial life understandable, clean and actionable.

## Core experience

### 1. Financial signal engine

Continuously derive relevant signals from existing FullWorth data:

- unusual spending changes
- recurring payments and contract changes
- duplicate or overlapping contracts
- subscriptions with weak usage/value signals
- budget drift
- upcoming liquidity pressure
- income changes
- savings-rate changes
- unusually expensive merchants/categories
- uncategorized or suspiciously categorized transactions
- internal transfers that are still misclassified
- net-worth changes that need explanation
- missing or stale data that weakens conclusions

Signals are deterministic whenever possible. AI can rank, explain and combine signals.

### 2. Personal financial model

Build one normalized model across:

- accounts
- transactions
- merchants
- categories
- contracts
- budgets
- income
- assets and liabilities
- portfolios
- spending reviews
- household/space context
- goals

This model is the source for Coach, proactive insights, simulations and automation rules.

### 3. Proactive insight inbox

Do not surface every detected fact.

Create a small ranked feed of things that are worth attention:

- "Your electricity cost increased by 31%."
- "These three contracts probably describe the same subscription."
- "At the current pace this budget will exceed its target by about 84 EUR."
- "Your 90-day monthly surplus fell by 240 EUR."
- "This merchant is still uncategorized although similar transactions were corrected before."

Each insight needs:

- title
- short explanation
- evidence
- confidence
- financial impact
- recommended next action
- dismiss / snooze / teach FullWorth

### 4. Actions, not only answers

Coach should be able to prepare safe actions:

- create or change a categorization rule
- merge duplicate contract candidates
- change transaction category
- mark internal transfers
- adjust a budget
- create a savings rule
- prepare a recurring-payment cancellation workflow
- create a reminder
- open the exact affected object

The assistant may prepare an action, but important writes require confirmation.

### 5. Natural-language rules

Users should be able to say things such as:

- "Amazon below 20 EUR is usually household, except Kindle."
- "Treat transfers to my savings account as internal transfers."
- "Warn me if eating out exceeds 250 EUR this month."
- "If my checking balance is above 2,000 EUR at month end, suggest moving the rest to savings."

Compile these into explicit, inspectable rules. Do not keep rules as opaque prompts.

### 6. Simulation

Add a deterministic simulation layer that AI can explain:

- Can I afford X?
- What happens if I save X more per month?
- When do I reach a target?
- What if a recurring cost increases/decreases?
- What would my monthly buffer be after a new contract?
- What changes if income drops for N months?

Calculations stay deterministic. AI turns assumptions and results into understandable language.

## Coach UX

### Empty chat

Do not show a row of random prompt chips above the conversation.

Show a compact starter panel only while the conversation is empty:

- one short heading
- three relevant suggestions
- each suggestion has a category label plus an actual question
- deterministic mode only offers capabilities that the deterministic engine supports
- AI mode may offer richer page-context questions

### During conversation

- hide starter suggestions after the first user message
- show follow-up questions directly below the assistant answer that produced them
- keep object actions separate from follow-up questions
- use "Local analysis" instead of exposing the implementation term "Deterministic" to normal users

### Context

Page context should influence AI prompts when AI is available.

Until the deterministic engine has dedicated object intents, deterministic suggestions must not pretend to understand a selected transaction/account/contract/budget individually.

## Execution phases

### Phase A — Coach credibility

- align starter suggestions with real deterministic intents
- replace top prompt-chip row with empty-state suggestions
- move follow-ups below answers
- replace technical "Deterministic" wording in the UI
- add tests that starter prompts resolve to concrete deterministic behavior

### Phase B — Insight foundation

- introduce normalized `FinancialSignal` model
- deterministic detectors for spending, contracts, budgets, cash flow and data quality
- rank by impact, confidence and novelty
- insight inbox API and UI
- feedback: useful / irrelevant / snooze / teach

### Phase C — Personal model and explanations

- combine account, transaction, contract, wealth and review facts
- generate compact evidence bundles
- AI explanation service over deterministic signals
- never send more user data to an external provider than the selected feature requires

### Phase D — Safe actions

- action proposal model
- preview/diff before write
- confirmation requirement
- audit trail
- idempotency
- rollback where feasible

### Phase E — Rules and automation

- natural-language-to-rule compiler
- visible rule representation
- dry-run against historical data
- user confirmation before activation
- event-driven evaluation instead of frequent global polling

### Phase F — Simulations

- deterministic scenario engine
- reusable assumptions
- compare scenarios
- Coach explanations on top

## Architecture guardrails

- AI provider failure must not break deterministic finance functionality.
- No automatic destructive action.
- No silent money movement.
- Every AI-originated write must be traceable.
- Prefer event-driven recalculation after imports/changes over minute-level polling.
- Cache stable derived facts.
- Keep user-specific learning local unless Cloud Intelligence consent explicitly permits a minimized contribution.
- Treat AI output as a proposal, not financial ground truth.
- Store structured evidence and action payloads separately from generated prose.

## Success criteria

FullWorth should feel smarter even if the user never opens Coach.

The strongest product signal is not chat usage. It is:

- fewer uncategorized/wrong transactions
- fewer duplicate contracts
- fewer missed relevant changes
- faster understanding of monthly finances
- more useful actions completed from insights
- high acceptance rate for suggestions
- low dismissal rate for proactive alerts
