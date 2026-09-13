# Juloc Agent Bootstrap

Before doing substantial work in this repository:

1. Read `.agent/project.yaml` for repository-specific commands, protected paths and related repositories.
2. Read the canonical current operating rules from `Juloc/agent-control/AGENTS.md` on `main` using the available GitHub access.
3. Follow those rules, including checking target Issues/PRs and cross-agent coordination in `Juloc/agent-control` issue #1 before claiming work.
4. Read any additional repository-specific instruction file named in `.agent/project.yaml` notes.

Do not ask the user to repeat the Agent Control protocol in their prompt.

## Fallback if the central repository cannot be read

Continue safely without blocking repository recovery:
- inspect this repository's open Issues and PRs before editing,
- avoid work that overlaps an active branch/PR,
- use GitHub Issues as the backlog instead of creating manual status/backlog Markdown,
- keep changes scoped and run the repository's configured validation,
- never put credentials or secrets in code, docs, issues or comments,
- record that cross-agent coordination could not be verified.

The Agent Hub is optional. GitHub remains the durable source of truth.
