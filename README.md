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

Supported environment variables are `WFX_PROVIDER`, `WFX_PROFILE`, `WFX_BASE_URL`, `WFX_API_KEY`, `WFX_MODEL`, `WFX_TIMEOUT_SECONDS`, `WFX_MAX_ITERATIONS`, and `WFX_APPROVAL`. `OPENAI_API_KEY` and `OPENROUTER_API_KEY` are provider-specific credential fallbacks.

### Profiles

A profile is a named settings layer stored under `profiles` in a config file. Profile entries accept the same keys as top-level configuration (`provider`, `base_url`, `api_key`, `model`, `headers`, `timeout_seconds`, `max_iterations`, `approval`):

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

Select a profile per invocation with `--profile <name>`, set a session default with `WFX_PROFILE`, or set a file default with a top-level `"profile"` key. Precedence is `--profile` > `WFX_PROFILE` > `"profile"` key, and the project file's default overrides the user file's. Profiles with the same name in both files merge key-by-key with the project winning; environment variables and CLI flags still override profile values. Selecting an undefined profile fails and lists every available profile.

```powershell
wfx --profile fast
wfx run --profile reasoning "Analyze the agent loop for cancellation bugs."
```

WFX echoes the active profile and model at startup.

A workspace-controlled `base_url` cannot inherit credentials or custom headers from user configuration or environment variables. This prevents a cloned repository from redirecting ambient secrets to its own endpoint. WFX prints a warning when it suppresses such credentials. To use credentials with a custom endpoint, configure both at user/environment/CLI scope, or explicitly place both in the project configuration.

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
