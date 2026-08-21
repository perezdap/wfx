# Risks and unknowns

| Risk | Milestone 1 treatment | Follow-up |
| --- | --- | --- |
| PowerShell cannot be classified perfectly from text | Conservative allowlist plus aliases; rooted/`..`/`$`/`&` scripts are `SystemChange` (never auto-approved `ReadOnly`); unknown scripts are `SystemChange`; root/disk operations including `rm`/`ri` are `Dangerous` | Use the PowerShell parser AST plus command metadata and an OS sandbox |
| Path check/operation race (TOCTOU) | Normalize and resolve links immediately before each operation; recursive tools avoid reparse points | Add handle-based Windows final-path validation and adversarial junction race tests |
| OpenAI-compatible endpoints vary | Small explicit Chat Completions SSE adapter; `stream_options` only for OpenAI/OpenRouter | Add endpoint capability profiles and recorded conformance fixtures |
| Tool output can exhaust context | Individual read/search limits, bounded result counts, and 1 MiB process stdout/stderr capture | Add a central byte/token budget and spill large output to artifacts |
| Patch format coverage | Exact-context unified-diff hunks for existing text files | Add create/delete/rename, newline metadata, and fuzz/property tests |
| Native AOT packages/toolchain vary by runner | AOT analyzers enabled and CI publishes on native x64/ARM64 Windows runners | Track size/startup/memory trends and pin known-good toolchains |
| Console prompt is not a security boundary | Host-owned approval service and noninteractive denial | Add signed/opaque approval requests for remote/ACP hosts |
| Interactive prompts do not yet preserve conversation history | Each prompt is an isolated agent run | Add explicit session storage behind `IAgentSession` after format design |

## Deliberately deferred

MCP, ACP, skills, subagents, native Anthropic/Gemini/Azure transports, session persistence, auto-update, WinGet packaging, a complex TUI, commits, pushes, and arbitrary Git commands are outside Milestone 1. Their absence keeps the security and provider foundations reviewable.
