---
name: wfx-models-sync
description: "Sync a provider's live /models catalog into wfx named profiles (%USERPROFILE%\\.wfx\\config.json) using tools/Sync-WfxProfiles.ps1. Use when the user wants to add, update, refresh, or list the available models of a wfx provider (venice, deepseek, gemini, qwen-token-plan, cursor, ollama, etc.) as selectable profiles."
---

# WFX Models Sync

Syncs a provider's live model catalog into wfx **named profiles** (issue #7's
feature): one profile per model under the `<provider>/<model-id>` namespace in
the wfx configuration file. Adapted from the pi `*-models-sync` skills, but
PowerShell-first (no Node.js) and targeting wfx's settings-layer config instead
of pi's `models.json`.

## Requirements

- PowerShell 7+.
- The provider's API key env var must be set **for the sync to read the
  catalog** (see the table). The synced profiles never contain secrets.

## Run

From the repo root:

```powershell
# Preview without writing
pwsh tools/Sync-WfxProfiles.ps1 venice -DryRun

# Sync into the user config (default: %USERPROFILE%\.wfx\config.json)
pwsh tools/Sync-WfxProfiles.ps1 deepseek

# Sync into a project config instead
pwsh tools/Sync-WfxProfiles.ps1 zai-coding -ConfigPath .\.wfx\config.json

# List the provider registry
pwsh tools/Sync-WfxProfiles.ps1 -ListProviders
```

## Provider registry

| Provider | Catalog endpoint | Key env var |
| --- | --- | --- |
| `abliteration` | api.abliteration.ai/v1 | `ABLITERATION_API_KEY` |
| `atlas-cloud` | api.atlascloud.ai/v1 | `ATLAS_CLOUD_API_KEY` |
| `cursor` | 127.0.0.1:8080/v1 (local proxy) | none |
| `deepseek` | api.deepseek.com | `DEEPSEEK_API_KEY` |
| `fireworks` | api.fireworks.ai/inference/v1 | `FIREWORKS_API_KEY` |
| `gemini` | generativelanguage.googleapis.com/v1beta (`v1beta/openai` shim written to profiles) | `GEMINI_API_KEY` |
| `inception` | api.inceptionlabs.ai/v1 | `INCEPTION_API_KEY` |
| `meta` | api.meta.ai/v1 | `META_AI_API_KEY` |
| `neuralwatt` | api.neuralwatt.com/v1 | `NEURALWATT_API_KEY` |
| `ollama` | 127.0.0.1:11434/v1 (local OpenAI-compat daemon) | none |
| `poe` | api.poe.com/v1 | `POE_API_KEY` |
| `qwen-token-plan` | token-plan.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1 | `QWEN_TOKEN_PLAN_API_KEY` |
| `routera` | api.routera.one/v1 | `ROUTERA_API_KEY` |
| `sakana` | api.sakana.ai/v1 | `SAKANA_API_KEY` |
| `venice` | api.venice.ai/api/v1 (text models only) | `VENICE_API_KEY` |
| `zai-coding` | api.z.ai/api/coding/paas/v4 | `ZAI_API_KEY` |

Not yet ported from the pi collection: the Cloudflare-AI-Gateway-routed
providers (`asterlab`, `nube`, `cloudflare-vertex`), Chatbox AI, and Cline.
Add them to the hashtable in `tools/Sync-WfxProfiles.ps1` when needed.
Registry entries are ported from the pi skills; only `venice`, `deepseek`,
`gemini`, `cursor`, and `ollama` have been verified live so far. The one
exception is `abliteration`, which is sourced from its own docs
(https://docs.abliteration.ai) and verified live as far as an unauthenticated
401 probe plus a keyed sync against a local fake catalog — the real API has
not been synced (no key available).

## Behavior to know before running

- **Sync owns the `<provider>/*` namespace.** Profiles under it are added,
  updated, or removed to match the catalog. Hand-written profiles should use
  other names; everything outside the namespace is preserved.
- **Secrets:** profiles carry `provider`, `base_url`, and `model` only. At wfx
  runtime, credentials come from `WFX_API_KEY` (any provider; `OPENAI_API_KEY`
  is the generic fallback for non-OpenRouter providers) or a hand-added
  `"api_key"` in the profile. The script never writes keys.
- **Writing normalizes the file to plain JSON.** Comments and trailing commas
  are tolerated on read but not preserved on write; when the catalog matches
  the managed profiles, the file is left untouched. Before overwriting, the
  previous file is saved to `config.json.bak` — the backup keeps any
  hand-written secrets the original held, so treat it as a secrets file.
- Useful flags: `-DryRun`, `-Include <regex[]>`, `-Exclude <regex[]>`,
  `-PreserveRouting` (keep existing provider/base_url), `-Prefix`,
  `-ModelsEndpoint`, `-BaseUrl`, `-EnvVar`, `-WhatIf`.

## Validation

After syncing, verify wfx resolves a synced profile:

```powershell
wfx config --profile venice/llama-3.3-70b
```

Expected: `Provider:`/`Profile:`/`Model:`/`Base URL:` lines resolve, and
`Credentials: not configured` unless a key is in scope. Never print or commit
API keys.
