# Agent Operating Rules

These rules are the default operating protocol for every Juloc repository that adopts Agent Control.

## 1. Startup
Before changing code or documentation:
1. Read this file completely.
2. Read `.agent/project.yaml` if present.
3. Inspect the target repository's relevant open issues and pull requests.
4. Check current coordination state in `Juloc/agent-control` issue #1. If an Agent Hub endpoint is configured and reachable, it may be used instead because it reads the same GitHub ledger.
5. Identify overlapping active work before choosing or claiming a task.

Do not require the user to restate these rules in prompts.

## 2. Sources of truth
- GitHub Issues: durable backlog, bugs, features and acceptance criteria.
- GitHub Pull Requests: implementation/review state.
- `Juloc/agent-control` issue #1: cross-agent coordination events and temporary claims.
- `AGENTS.md` + `.agent/project.yaml`: working rules and repository-specific commands.
- Architecture docs / ADRs: durable technical decisions.

Do not create or maintain separate manual `BACKLOG.md`, `STATUS.md`, `CURRENT_WORK.md` or similar files as competing state stores.

## 3. Coordination
Assume Claude, Codex, ChatGPT and other agents can work concurrently.

Before substantial edits, create a coordination claim. Claims are advisory coordination locks with a finite lease, not permanent ownership.

A claim must identify:
- agent
- repository
- task/issue when available
- intended path/component scope
- lease duration

If another live claim overlaps materially, do not silently work over it. Prefer a different task/scope, coordinate through GitHub, or explicitly record why overlap is safe.

Re-check coordination before pushing or merging substantial changes.

## 4. Hub fallback
The Agent Hub is an optimization, not a dependency.

If the Hub is unavailable, use the GitHub coordination ledger directly. If the central ledger is also unavailable, inspect target-repository PRs/issues and clearly record that cross-agent coordination could not be verified. Never block normal repository recovery merely because the Hub is down.

## 5. Backlog discipline
When discovering a real problem:
1. Search for an existing issue first.
2. Update the existing issue when it already covers the problem.
3. Otherwise create one focused issue with context, expected outcome and acceptance criteria.
4. Link dependencies instead of duplicating them.

Do not create speculative issues merely to make a backlog look complete.

## 6. Working rules
- Keep changes scoped to the selected task.
- Avoid unrelated cleanup unless it is required for correctness.
- Record meaningful blockers or scope changes; do not spam progress comments.
- Prefer small, reviewable branches/PRs.
- Respect protected paths and validation commands from `.agent/project.yaml`.
- Never put credentials, tokens or secrets into repository files, issues or coordination events.

## 7. Completion
A task is complete only when:
- acceptance criteria are satisfied,
- required validation passes or limitations are documented,
- affected durable documentation is updated,
- PR/issue state reflects reality,
- the coordination claim is released/completed.

## 8. Documentation
Update documentation when behavior, architecture, deployment or operator expectations changed. Do not rewrite docs only to mirror temporary task status.

## 9. Coordination event format
GitHub-only agents use the event format defined in `Juloc/agent-control/docs/PROTOCOL.md`. Human prompts should remain simple; the agent is responsible for producing the required coordination event itself.
