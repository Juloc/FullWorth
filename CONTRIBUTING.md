# Contributing

Keep changes small, tested and documented when they alter user-visible behaviour or operations.

- Preserve the security boundaries described in [docs/SECURITY_ARCHITECTURE.md](docs/SECURITY_ARCHITECTURE.md).
- Never commit secrets, backups or real financial data.
- Run the relevant build and tests before opening a pull request.
- All new and touched C# code must follow [docs/CODING_CONVENTIONS.md](docs/CODING_CONVENTIONS.md), including the canonical Juloc 230/280 line-width and no-meaningless-forwarding-method rules.
- Update the focused document in [docs](docs/README.md) instead of adding project-status or handover notes.
- Keep GitHub Actions cost-conscious: CI and other routine workflows must be manual-only (`workflow_dispatch`) unless explicitly approved.
- Version-tag releases are the intended exception: pushing a `v*` tag may automatically run the release workflow and publish images.

