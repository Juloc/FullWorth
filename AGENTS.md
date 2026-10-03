# Juloc Agent Bootstrap

Before doing substantial work in this repository:

1. Read `.agent/project.yaml` for repository-specific commands, protected paths and related repositories.
2. Read the canonical current operating rules from `Juloc/agent-control/AGENTS.md` on `main` using the available GitHub access.
3. Read `docs/MAINTAINABILITY_CONVENTIONS.md` and the canonical maintainability document it references before implementation work.
4. Follow those rules, including checking target Issues/PRs and cross-agent coordination in `Juloc/agent-control` issue #1 before claiming work.
5. Read any additional repository-specific instruction file named in `.agent/project.yaml` notes.

Do not ask the user to repeat the Agent Control protocol in their prompt.

## Mandatory maintainability enforcement

The central maintainability discipline and FullWorth's local `docs/MAINTAINABILITY_CONVENTIONS.md` are binding for every implementation task.

- Preserve the repository's established layer ownership and guards; do not bypass them with parallel code paths.
- Search for the canonical owner before creating new services, stores, helpers, interfaces, factories, providers, managers, handlers, configuration paths or dependencies.
- Keep control flow readable and reject artificial micro-methods, forwarding wrappers and hidden side effects.
- Comments explain non-obvious intent/constraints, not obvious code.
- When a structural invariant is deterministic and safely machine-checkable, add or extend an architecture/source/CI guard.
- Run the canonical maintainability completion gate before reporting work complete. A new violation in intentionally touched code means the task is not complete.

## Fallback if the central repository cannot be read

Continue safely without blocking repository recovery:
- inspect this repository's open Issues and PRs before editing,
- avoid work that overlaps an active branch/PR,
- use GitHub Issues as the backlog instead of creating manual status/backlog Markdown,
- keep changes scoped and run the repository's configured validation,
- never put credentials or secrets in code, docs, issues or comments,
- record that cross-agent coordination could not be verified.

The Agent Hub is optional. GitHub remains the durable source of truth.
