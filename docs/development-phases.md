# Milestone 1 implementation phases (historical)

> Milestone 1 is complete (tag `v0.1.0`). The phases below are retained as the historical record of how it was built; they are not a forward-looking roadmap. Features shipped since are recorded in the ADRs under [adr/](adr/).

1. **Contracts and state** — model messages/events, tool registry, approval service, context providers, and explicit agent loop.
2. **Windows execution and security** — workspace discovery/path policy, cancellable process runner, and dedicated PowerShell subsystem.
3. **Vertical tool slice** — read/write/list/search/patch, conservative PowerShell, and bounded read-only Git.
4. **Provider and configuration** — explicit OpenAI-compatible streaming protocol plus layered settings and redaction.
5. **CLI experience** — interactive/run/models/config commands, progress observer, and approval prompts.
6. **Verification and distribution** — deterministic fake-provider tests, Windows process tests, Native AOT matrix, and artifact measurements.

Each phase must leave all earlier tests green. Live paid APIs are excluded from automated tests; a configurable endpoint smoke test is a release/manual verification step.
