# WFX

WFX is a small, embeddable, Windows-first AI coding-agent runtime. It is designed for native Windows and PowerShell workflows and ships as a self-contained executable: no WSL, Docker, Node.js, or Python runtime is required.

> Status: Milestone 1 complete. Review approval prompts before allowing changes in important workspaces.

## What works

- Interactive mode and single-task `wfx run` mode, each persisted by default as an append-only JSONL session log
- Streaming OpenAI-compatible Chat Completions transport
- OpenAI, OpenRouter, LM Studio, Ollama-compatible, and custom endpoints
- Structured `read_file`, `write_file`, `apply_patch`, `list_directory`, `search_files`, `search_text`, `powershell`, and `git` tools
- Dedicated PowerShell execution with `pwsh.exe` preference, Windows PowerShell fallback, cancellation, timeout, environment, stdout, stderr, and exit codes. Child processes omit secret-bearing variables by default and set `GIT_PAGER=cat` / `PAGER=cat` so tool-driven git cannot block on a pager.
- Link-aware workspace boundary checks and conservative approvals
- Root-to-working-directory `AGENTS.md` discovery
- Layered JSON and environment configuration with named profiles
- Session listing and resumption with `wfx sessions` and `wfx resume` (see [Use](#use))
- User-configured MCP servers over stdio and Streamable HTTP with OAuth 2.1 sign-in (see [MCP servers](#mcp-servers))
- Machine-readable `--json` event streams and result objects (see [Machine-readable output](#machine-readable-output))
- One ordered stderr stream for human output with markdown rendering, plus a `--quiet` flag (see [Human output and stdout](#human-output-and-stdout))
- Native AOT publish configuration for `win-x64` and `win-arm64`

## Build

Prerequisites:

- .NET 10 SDK
- For Native AOT on Windows: Visual Studio Build Tools with the Desktop development with C++ workload
- PowerShell 7 is recommended; Windows PowerShell is the fallback

```powershell
dotnet restore .\Wfx.sln
dotnet test .\Wfx.sln -c Release
dotnet publish .\src\Wfx.Cli\Wfx.Cli.csproj -c Release -r win-x64
```

The Native AOT executable is under `src\Wfx.Cli\bin\Release\net10.0\win-x64\publish\wfx.exe`.

## Configure

Credentials should come from environment variables:

```powershell
$env:WFX_API_KEY = "..."
$env:WFX_MODEL = "gpt-5"
```

Configuration is merged from lowest to highest precedence:

1. built-in defaults
2. `%USERPROFILE%\.wfx\config.json`
3. `<workspace>\.wfx\config.json`
4. `WFX_*` environment variables
5. CLI arguments

Example project configuration:

```json
{
  "provider": "openrouter",
  "model": "anthropic/claude-sonnet-4.6",
  "approval": "workspace",
  "timeout_seconds": 300
}
```

`timeout_seconds` bounds two phases of a provider request: waiting for response headers, and each gap between stream events after headers arrive. The idle window resets on every event, so a long generation that keeps producing output is never cut off; the request fails only if headers do not arrive in time or the stream goes silent for longer than the configured value.

For LM Studio:

```powershell
wfx run `
  --provider local `
  --base-url http://localhost:1234/v1 `
  --model qwen3-coder `
  "Inspect this repository and tell me what it does."
```

The shorthand below selects OpenRouter and passes the remaining identifier as the model:

```powershell
wfx --model openrouter/anthropic/claude-sonnet-4.6
```

Supported environment variables are `WFX_PROVIDER`, `WFX_PROTOCOL`, `WFX_PROFILE`, `WFX_BASE_URL`, `WFX_API_KEY`, `WFX_MODEL`, `WFX_TIMEOUT_SECONDS`, `WFX_MAX_ITERATIONS`, and `WFX_APPROVAL`. `OPENAI_API_KEY`, `OPENROUTER_API_KEY`, and `ANTHROPIC_API_KEY` are provider-specific credential fallbacks.

### Protocol

`protocol` selects the wire format spoken to the model endpoint. It is settable in config files, via `WFX_PROTOCOL`, and via `--protocol`. The default is `chat_completions`, so existing configurations behave identically.

| Protocol | Status | Default endpoint | Credential fallback |
| --- | --- | --- | --- |
| `chat_completions` | implemented (default) | provider preset | provider preset |
| `responses` | implemented | `https://api.openai.com/v1` | `OPENAI_API_KEY` |
| `anthropic_messages` | reserved | `https://api.anthropic.com/v1` | `ANTHROPIC_API_KEY` |

`anthropic_messages` fails with an explicit "not implemented yet" error. Unknown protocol values fail with a clear error naming the valid values.

`protocol: responses` runs the agent over the OpenAI Responses API: conversation state is sent as Responses input items, text and tool-call arguments stream from the typed Responses events, and tool results round-trip as `function_call_output` items. Requests are stateless (`store: false`), so the full conversation is replayed each turn rather than kept server-side. A stream that fails, truncates (`response.incomplete`), or ends without a completion event fails the turn with an explicit protocol error rather than returning a partial answer.

Reasoning models are supported. Because a stateless client must send every prior output item back, the transport records each finished turn exactly as the endpoint expressed it and replays those items verbatim on the following request, and asks for `reasoning.encrypted_content` so the reasoning chain survives a tool call. Turns the endpoint did not describe item-by-item are rebuilt from text and tool calls as before.

```powershell
wfx run --protocol responses --model gpt-5.1 "Summarize this repo."
```

Or in config:

```json
{
  "protocol": "responses",
  "model": "gpt-5.1"
}
```

Provider remains orthogonal to protocol. The `anthropic` provider preset targets Anthropic's OpenAI-compatible endpoint (`https://api.anthropic.com/v1`) with `ANTHROPIC_API_KEY`:

```powershell
wfx run --provider anthropic --model claude-sonnet-4-6 "Summarize this repo."
```

Or in config:

```json
{
  "provider": "anthropic",
  "model": "claude-sonnet-4-6"
}
```

### Profiles

A profile is a named settings layer stored under `profiles` in a config file. Profile entries accept the same keys as top-level configuration (`provider`, `protocol`, `base_url`, `api_key`, `model`, `headers`, `timeout_seconds`, `max_iterations`, `approval`):

```json
{
  "profiles": {
    "fast": {
      "provider": "openai",
      "model": "gpt-5-mini"
    },
    "reasoning": {
      "provider": "openrouter",
      "model": "anthropic/claude-sonnet-4.6",
      "max_iterations": 40
    }
  }
}
```

Select a profile per invocation with `--profile <name>`, set a session default with `WFX_PROFILE`, or set a file default with a top-level `"profile"` key. Precedence is `--profile` > `WFX_PROFILE` > `"profile"` key, and the project file's default overrides the user file's. When a profile is selected, its keys override the top-level keys of the same file. Profiles with the same name in both files merge key-by-key with the project winning (`headers` sets replace wholesale, as with ordinary layers); environment variables and CLI flags still override profile values. Selecting an undefined profile fails and lists every available profile. Profile names match case-insensitively, and two entries differing only by case are rejected as duplicates.

```powershell
wfx --profile fast
wfx run --profile reasoning "Analyze the agent loop for cancellation bugs."
```

WFX echoes the active profile and model at startup.

To populate profiles from a provider's live model catalog, run `tools\Sync-WfxProfiles.ps1` (see the `wfx-models-sync` skill under `.agents/skills/`):

```powershell
pwsh tools\Sync-WfxProfiles.ps1 venice -DryRun
pwsh tools\Sync-WfxProfiles.ps1 deepseek
```

Synced profiles land under a `<provider>/<model-id>` namespace and never contain credentials.

A workspace-controlled `base_url` cannot inherit credentials or custom headers from user configuration or environment variables. This prevents a cloned repository from redirecting ambient secrets to its own endpoint. WFX prints a warning when it suppresses such credentials. To use credentials with a custom endpoint, configure both at user/environment/CLI scope, or explicitly place both in the project configuration.

## Sessions

Interactive mode and `wfx run` write an append-only JSONL event log under `%USERPROFILE%\.wfx\sessions\` as the turn progresses. The filename is the session ID (`yyyyMMddTHHmmssZ-` plus 6 characters). Pass `--no-session` to skip persistence for that invocation.

The sessions directory is created with a Windows ACL granting only the current user. Known secret shapes in tool output are masked at ingestion, but redaction is not a guarantee: session files remain sensitive and should be treated as plaintext credentials-adjacent data.

Session pruning is not in this slice; resume and listing are covered under [Use](#use).

## Use

```powershell
cd C:\src\project
wfx
```

Interactive commands:

- `/model` lists every profile that has a `model` key as `profile/provider: model`, then prompts for a numbered selection. The selected profile's connection is adopted when it differs from the current connection.
- `/model <id>` switches to a free-form model ID on the current connection.
- `/help` lists interactive commands.
- `/exit` and `/quit` end the session.

WFX echoes the active profile and model after a successful switch. Conversation history is retained across model, provider, and protocol switches. Provider-specific history that cannot cross protocols is mapped to portable text and tool-call state when possible.

Or run one task:

```powershell
wfx run "Inspect this repository and tell me what it does."
wfx run "Find the failing Pester tests and fix them."
```

Inspect effective, secret-redacted configuration:

```powershell
wfx models
wfx config
wfx --help
```

List persisted sessions with workspace, timestamps, and size:

```powershell
wfx sessions
```

Resume the most recently updated session for the current workspace:

```powershell
wfx resume
```

Resume a specific session by ID:

```powershell
wfx resume --id <session-id>
```

A session remains bound to its recorded workspace. Resuming it elsewhere refuses and prints that path. To deliberately rebind a moved or renamed workspace, select the session explicitly with `wfx resume --id <session-id> --force`. Only one process can hold a session lease at a time, while `wfx sessions` remains available for inspection.

## MCP servers

WFX acts as an MCP host. Servers are declared **in the user configuration file only** (`%USERPROFILE%\.wfx\config.json`), never in a project config - a `mcp_servers` key in project config is a configuration error, so a cloned repository cannot launch executables or redirect credentials. Each entry defines exactly one transport, discriminated by key presence:

```json
{
  "mcp_servers": {
    "local-tool": { "command": "node", "args": ["server.js"] },
    "remote-tool": { "url": "https://agent.example.com/mcp" }
  }
}
```

`command` selects stdio (optional `args`/`env`); `url` selects Streamable HTTP (optional `headers`). Both or neither is a configuration error naming the rule. To the model, a remote server is indistinguishable from a local one: tools appear namespaced as `mcp_<server>_<tool>`.

Every MCP call is classified `SystemChange` unconditionally and approved per call through the ordinary approval service - even under `--yolo`, which skips prompts but not workspace policy.

### OAuth sign-in

Remote servers that require OAuth 2.1 are authorized with an explicit command, never mid-turn:

```powershell
wfx mcp auth <server>          # authorization-code + PKCE, loopback browser redirect
wfx mcp auth --revoke <server> # drop the stored credential
```

Tokens are stored at `%USERPROFILE%\.wfx\mcp-tokens.json` and refreshed inline. A 401 with no usable credential surfaces a sign-in remediation on stderr (never suppressed by `--quiet`); a 403 is a structured non-2xx failure, not an auth challenge.

See [ADR 0007](docs/adr/0007-mcp-servers-are-user-configured-never-workspace-supplied.md) for the trust boundary and [ADR 0008](docs/adr/0008-mcp-http-transport-and-oauth-sign-in.md) for the transport and sign-in design.

## Approval modes

| Mode | Read-only | Workspace write | System change | Dangerous |
| --- | --- | --- | --- | --- |
| `always` | allow | prompt | prompt | prompt |
| `workspace` | allow | allow | prompt | prompt |
| `never` | allow | deny | deny | deny |
| `yolo` | allow | allow | allow | allow |

PowerShell is classified conservatively. Known inspection commands are read-only, build/test commands are workspace writes, known system-management commands are system changes, destructive disk/root operations are dangerous, and unrecognized scripts default to system change. `Env:` provider reads and `$env:` access are system changes, never read-only.

## Architecture

The CLI is only a composition root. Application code can construct `Wfx.Core.Agent` directly with a model provider, tool registry, approval service, context provider, and observer. See [architecture.md](docs/architecture.md) for the contracts and dependency direction.

WFX takes inspiration from the small, native, model-agnostic philosophy of [Vercel Labs fx](https://github.com/vercel-labs/fx), but it is an independent Windows/.NET design and does not copy fx's implementation.

## Security boundaries

- Paths are normalized, checked lexically, and checked again after resolving existing symlinks/junctions.
- Recursive tools do not traverse reparse points and skip `.git`, `bin`, and `obj`.
- Git tool operations are restricted to `status`, `diff`, staged diff, and bounded `log`.
- The CLI never commits or pushes.
- API keys are not printed and are redacted if an endpoint echoes one in an error. Approval prompts, tool-call summaries, rejection reasons, and debug tool output replace known provider secrets with `[REDACTED]`. Known secret shapes in tool output are masked before they reach the model, memory, or a session file. Session files remain sensitive despite that matcher.
- Child processes omit secret-bearing environment variables by default: names matching `*_API_KEY`, `*_TOKEN`, or `*_SECRET`, plus `WFX_API_KEY`, `OPENAI_API_KEY`, and `OPENROUTER_API_KEY`. The `powershell` tool can restore specific parent variables with `inherit_environment`, which is classified as at least a system change. Scrubbed values are never logged.
- Child processes set `GIT_PAGER=cat` and `PAGER=cat` by default. An explicit process-environment overlay still wins.
- `--approval never` means deny mutations rather than silently execute them.
- `--approval yolo` (or `--yolo`) bypasses tool approval prompts. Workspace path checks still apply. Use it only in a workspace you are willing to lose. Do not put `yolo` in a shared project config.

See [risks.md](docs/risks.md) for remaining limitations.

## Human output and stdout

Human-facing output - model text and tool-call lines - goes to **stderr** as one ordered stream. Stdout receives the final response once, at turn end, and only when redirected, so `wfx run ... > notes.md` captures the answer alone. Blocks are separated by blank lines, tool calls indent by two, and decoration draws from the basic eight ANSI colours so the terminal theme resolves the hues. Markdown (bold, inline code, ATX headings, bullets, fences) renders through a hold-back scanner that keeps token-by-token streaming intact; with decoration suppressed the writer is a pass-through. See ADRs [0009](docs/adr/0009-decoration-uses-the-basic-eight-ansi-colours.md)-[0011](docs/adr/0011-human-output-is-a-stderr-stream.md).

`--quiet` suppresses human decoration on stderr in interactive mode and on the `run`, `resume`, `sessions`, `config`, and `models` commands. Warnings, errors, and the MCP sign-in remediation still use stderr.

## Machine-readable output

`wfx run --json` and `wfx resume --json` stream one newline-delimited JSON (NDJSON) event per line to stdout; `wfx sessions --json`, `wfx config --json`, and `wfx models --json` each write a single JSON result object. Shapes carry `schema_version` 1 and are published under [docs/schemas/](docs/schemas/). The stream is credential-adjacent - review it before sending it to shared logs.

## License

WFX is licensed under the [MIT License](LICENSE).
