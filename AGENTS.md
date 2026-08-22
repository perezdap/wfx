# WFX contributor instructions

WFX is a Windows-first, PowerShell-first .NET 10 coding-agent runtime.

## Required verification

Before describing a change as complete:

1. Run `dotnet test Wfx.sln -c Release`.
2. Publish `Wfx.Cli` with Native AOT for the affected Windows RID.
3. Run the freshly published `wfx.exe --help` and one relevant happy path.
4. Report any verification that the current environment could not perform.

Never claim a live model call passed unless it was actually exercised against the configured endpoint.

## Architecture boundaries

- `Wfx.Core` owns the agent loop, contracts, explicit state, configuration, approvals, instructions, and workspace policy. It must not depend on the CLI or a concrete provider/tool.
- `Wfx.Providers` owns model transport and protocol adaptation. It must not own agent state or tool execution.
- `Wfx.Tools` owns built-in tool implementations. Every tool must publish a JSON schema, classify approval before execution, remain inside the workspace, and support cancellation.
- `Wfx.PowerShell` owns child-process and PowerShell execution. Do not replace it with a generic Bash-oriented shell abstraction.
- `Wfx.Cli` is the composition root and console presentation layer. Product logic does not belong there.

Keep MCP, ACP, skills, subagents, WinGet, Pester, and other future capabilities behind contracts or tools. Do not add placeholder abstractions until a vertical slice needs them.

## Engineering rules

- Async and cancellation propagate through model and tool calls.
- Treat warnings as errors.
- Avoid reflection-heavy dependencies and dynamic JSON serialization. Maintain Native AOT analyzer cleanliness.
- Prefer typed records internally and explicit protocol serialization at boundaries.
- Never log API keys, authorization headers, or unredacted provider secrets.
- Never weaken path checks for convenience. Add boundary tests for every path-handling change.
- Unknown PowerShell behavior is sensitive; classify conservatively.
- Git writes, commits, pushes, and network operations are not part of the Milestone 1 Git tool.
- Add deterministic tests that do not require a paid or live model API.
- Pass multi-line `gh` issue and PR bodies with `--body-file`. Keep `pwsh -Command` argv to a short git/gh invocation; AMSI scans the entire string.

## Style

- Use file-scoped namespaces and nullable reference types.
- Favor small sealed types and composition.
- Keep public APIs minimal and XML documentation focused on SDK-facing contracts once the SDK surface stabilizes.
- CLI flags use kebab-case; configuration properties use snake_case.

## Agent skills

### Issue tracker

Issues live in GitHub Issues for `perezdap/wfx`, managed via the `gh` CLI. See `docs/agents/issue-tracker.md`.

### Triage labels

Default five-role vocabulary (`needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, `wontfix`). See `docs/agents/triage-labels.md`.

### Domain docs

Single-context layout: root `CONTEXT.md` and `docs/adr/`, created lazily by `/domain-modeling`. See `docs/agents/domain.md`.
