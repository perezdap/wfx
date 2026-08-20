# WFX

WFX is a small, embeddable, Windows-first AI coding-agent runtime. It is designed for native Windows and PowerShell workflows and ships as a self-contained executable: no WSL, Docker, Node.js, or Python runtime is required.

> Status: Milestone 1 foundation. Review approval prompts before allowing changes in important workspaces.

## What works

- Interactive mode and single-task `wfx run` mode
- Streaming OpenAI-compatible Chat Completions transport
- OpenAI, OpenRouter, LM Studio, Ollama-compatible, and custom endpoints
- Structured `read_file`, `write_file`, `apply_patch`, `list_directory`, `search_files`, `search_text`, `powershell`, and `git` tools
- Dedicated PowerShell execution with `pwsh.exe` preference, Windows PowerShell fallback, cancellation, timeout, environment, stdout, stderr, and exit codes
- Link-aware workspace boundary checks and conservative approvals
- Root-to-working-directory `AGENTS.md` discovery
- Layered JSON and environment configuration
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

Supported environment variables are `WFX_PROVIDER`, `WFX_BASE_URL`, `WFX_API_KEY`, `WFX_MODEL`, `WFX_TIMEOUT_SECONDS`, `WFX_MAX_ITERATIONS`, and `WFX_APPROVAL`. `OPENAI_API_KEY` and `OPENROUTER_API_KEY` are provider-specific credential fallbacks.

## Use

```powershell
cd C:\src\project
wfx
```

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

## Approval modes

| Mode | Read-only | Workspace write | System change | Dangerous |
| --- | --- | --- | --- | --- |
| `always` | allow | prompt | prompt | prompt |
| `workspace` | allow | allow | prompt | prompt |
| `never` | allow | deny | deny | deny |

PowerShell is classified conservatively. Known inspection commands are read-only, build/test commands are workspace writes, known system-management commands are system changes, destructive disk/root operations are dangerous, and unrecognized scripts default to system change.

## Architecture

The CLI is only a composition root. Application code can construct `Wfx.Core.Agent` directly with a model provider, tool registry, approval service, context provider, and observer. See [architecture.md](docs/architecture.md) for the contracts and dependency direction.

WFX takes inspiration from the small, native, model-agnostic philosophy of [Vercel Labs fx](https://github.com/vercel-labs/fx), but it is an independent Windows/.NET design and does not copy fx's implementation.

## Security boundaries

- Paths are normalized, checked lexically, and checked again after resolving existing symlinks/junctions.
- Recursive tools do not traverse reparse points and skip `.git`, `bin`, and `obj`.
- Git tool operations are restricted to `status`, `diff`, staged diff, and bounded `log`.
- The CLI never commits or pushes.
- API keys are not printed and are redacted if an endpoint echoes one in an error.
- `--approval never` means deny mutations rather than silently execute them.

See [risks.md](docs/risks.md) for remaining limitations.
