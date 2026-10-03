# Maintainability and AI Coding Discipline

FullWorth uses the canonical Juloc maintainability rules defined in [Juloc/agent-control `docs/MAINTAINABILITY_CONVENTIONS.md`](https://github.com/Juloc/agent-control/blob/main/docs/MAINTAINABILITY_CONVENTIONS.md).

They are mandatory for every implementation task and for all new or intentionally touched code. They are not optional recommendations.

FullWorth-specific enforcement summary:

- Preserve the existing layer contract: `*Endpoints.cs` handles transport, `*Store.cs` owns database access, and `*Service.cs` exists only for a real multi-store/external flow.
- Before adding a service, store, helper, interface, provider, manager, handler, configuration path or dependency, inspect the existing FullWorth owner/pattern first.
- Do not bypass or duplicate existing architecture guards such as layer, module-boundary, route-surface and parity/structure tests.
- Keep coherent control flow readable top-to-bottom. Do not create artificial tiny helpers, wrappers or forwarding methods.
- Names and effects must match. Reads/validation do not hide unrelated mutations.
- One business rule has one canonical owner.
- Do not swallow errors, use broad catch-all fallbacks, or silently continue with default/legacy paths.
- Propagate cancellation and use real timeouts for I/O-bound work.
- Avoid N+1 queries, whole-table application filtering, unnecessary mappings/allocations and speculative abstraction in hot paths.
- New dependencies/technologies require a concrete present need.
- Comments explain why, constraints and non-obvious intent; they do not narrate obvious code.
- Tests protect behavior and regressions, not private implementation wiring.
- Do not accumulate Legacy/Old/V2/fallback runtime branches. Migrate and remove obsolete paths within the supported upgrade window.
- When a structural rule is objectively and safely machine-checkable, add or extend a guard/test so CI can reject regressions.
- The canonical maintainability completion gate must be run before any AI agent reports work complete.

A change that introduces a new violation in intentionally touched code is not complete.
