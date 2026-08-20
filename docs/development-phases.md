# Milestone 1 implementation phases

1. **Contracts and state** — model messages/events, tool registry, approval service, context providers, and explicit agent loop.
2. **Windows execution and security** — workspace discovery/path policy, cancellable process runner, and dedicated PowerShell subsystem.
3. **Vertical tool slice** — read/write/list/search/patch, conservative PowerShell, and bounded read-only Git.
4. **Provider and configuration** — explicit OpenAI-compatible streaming protocol plus layered settings and redaction.
5. **CLI experience** — interactive/run/models/config commands, progress observer, and approval prompts.
6. **Verification and distribution** — deterministic fake-provider tests, Windows process tests, Native AOT matrix, and artifact measurements.

Each phase must leave all earlier tests green. Live paid APIs are excluded from automated tests; a configurable endpoint smoke test is a release/manual verification step.
