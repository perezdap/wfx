# Risks and unknowns

| Risk | Milestone 1 treatment | Follow-up |
| --- | --- | --- |
| PowerShell cannot be classified perfectly from text | Conservative allowlist plus aliases; rooted/`..`/`$`/`&`/`Env:` scripts are `SystemChange` (never auto-approved `ReadOnly`); unknown scripts are `SystemChange`; root/disk operations including `rm`/`ri` are `Dangerous` | Use the PowerShell parser AST plus command metadata and an OS sandbox |
| Child processes inherit the agent host environment | `ProcessExecutor` scrubs secret-bearing variable names (`*_API_KEY`/`*_TOKEN`/`*_SECRET` and the documented credential names), sets `GIT_PAGER=cat` and `PAGER=cat`, then applies overlay; `inherit_environment` is an explicit, `SystemChange` opt-in. Values are never logged | Broader secret-name heuristics and a Windows job-object sandbox |
| Path check/operation race (TOCTOU) | Normalize and resolve links immediately before each operation; recursive tools avoid reparse points | Add handle-based Windows final-path validation and adversarial junction race tests |
| OpenAI-compatible endpoints vary | Small explicit Chat Completions SSE adapter; `stream_options` only for OpenAI/OpenRouter | Add endpoint capability profiles and recorded conformance fixtures |
| Tool output can exhaust context | Individual read/search limits, bounded result counts, and 1 MiB process stdout/stderr capture | Add a central byte/token budget and spill large output to artifacts |
| Patch format coverage | Exact-context unified-diff hunks for existing text files | Add create/delete/rename, newline metadata, and fuzz/property tests |
| Native AOT packages/toolchain vary by runner | AOT analyzers enabled and CI publishes on native x64/ARM64 Windows runners | Track size/startup/memory trends and pin known-good toolchains |
| Console prompt is not a security boundary | Host-owned approval service and noninteractive denial | Add signed/opaque approval requests for remote/ACP hosts |
| Shared endpoints can rate-limit long runs | Transient 429/5xx are retried with bounded jittered backoff, honoring `Retry-After`; the overall timeout bounds the total wait | Retry transport-level failures and mid-stream drops; report retry counts to the observer |
| Session files remain sensitive despite redaction | Prefix-anchored masking at tool-result ingestion plus a current-user ACL on `%USERPROFILE%\.wfx\sessions\` | Resume/list/prune; optional substitution of known configured secret values |

## Deliberately deferred

MCP, ACP, skills, subagents, native Anthropic/Gemini/Azure transports, session resume/list/prune, auto-update, WinGet packaging, a complex TUI, commits, pushes, and arbitrary Git commands are outside Milestone 1. Their absence keeps the security and provider foundations reviewable.
