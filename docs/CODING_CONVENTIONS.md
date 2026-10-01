# C# Coding Conventions

FullWorth uses the canonical Juloc C# conventions defined in [Juloc/agent-control `docs/CSHARP_CONVENTIONS.md`](https://github.com/Juloc/agent-control/blob/main/docs/CSHARP_CONVENTIONS.md).

These rules are mandatory for all new C# code and whenever existing C# code is intentionally changed. Existing untouched code does not require a bulk style-only rewrite.

The non-negotiable project defaults include:

- 230-character soft line-width limit and 280-character hard limit.
- Prefer compact readable horizontal code over unnecessary vertical argument/signature wrapping.
- Microsoft/.NET naming conventions: PascalCase/camelCase, `_camelCase` instance fields, `s_camelCase` static fields, `I...` interfaces, `Async` asynchronous method suffixes, affirmative boolean names and plural collection names.
- Allman braces, four-space indentation, no tabs, one statement/declaration per line and no blank-line inflation.
- No meaningless forwarding micro-methods. A separate method must add behavior, policy, reuse, complexity isolation or a real contract/boundary. Genuine overload forwarding is allowed.
- Expression-bodied members and lambdas are allowed where they are the clearest representation; `=>` must not be used to justify a pointless forwarding method.

The canonical document contains the complete rules, examples and links to the Microsoft/.NET sources from which the baseline is derived.
