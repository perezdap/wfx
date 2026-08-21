#Requires -Version 7.0
<#
.SYNOPSIS
    Sync a provider's live /models catalog into wfx profiles.

.DESCRIPTION
    Fetches an OpenAI-compatible /models endpoint (or Gemini's model list) and
    writes one wfx profile per model into the user configuration file, under
    the "<provider>/<model-id>" namespace. Sync owns that namespace: profiles
    under it are added, updated, or removed to match the catalog. Everything
    else in the file — top-level keys and hand-written profiles — is preserved.

    Profiles never carry secrets. Set the provider's API key via an environment
    variable at wfx runtime (WFX_API_KEY works with any provider and
    OPENAI_API_KEY is the generic fallback for non-OpenRouter providers), or add
    "api_key" to a profile by hand.

    Writing normalizes the file to plain JSON: comments and trailing commas are
    tolerated on read but not preserved on write. The previous file is saved to
    <config>.bak before overwriting — note the backup keeps any hand-written
    secrets the original contained. When the catalog matches the managed
    profiles already, the file is left untouched.

.EXAMPLE
    pwsh tools/Sync-WfxProfiles.ps1 venice -DryRun

.EXAMPLE
    pwsh tools/Sync-WfxProfiles.ps1 deepseek

.EXAMPLE
    pwsh tools/Sync-WfxProfiles.ps1 -ListProviders
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Position = 0)]
    [string]$Provider,

    [string]$ConfigPath = (Join-Path $env:USERPROFILE '.wfx' 'config.json'),

    [string]$EnvVar,
    [string]$BaseUrl,
    [string]$ModelsEndpoint,
    [string]$Prefix,
    [string[]]$Include,
    [string[]]$Exclude,
    [switch]$PreserveRouting,
    [switch]$DryRun,
    [switch]$ListProviders
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# --- Provider registry -----------------------------------------------------
# Each entry: BaseUrl (also the default /models endpoint and the value written
# to profiles), EnvVar for the API key ($null = no auth, e.g. a local proxy),
# Filter (scriptblock over a raw catalog entry), Exclude (regex over the id).
# Endpoints are ported from the pi *-models-sync skills; verified live so far:
# venice, deepseek, gemini (x-goog-api-key listing + v1beta/openai shim), and
# cursor against a local fake endpoint.
$script:ProviderRegistry = [ordered]@{
    'atlas-cloud'     = @{ BaseUrl = 'https://api.atlascloud.ai/v1';          EnvVar = 'ATLAS_CLOUD_API_KEY' }
    'cursor'          = @{ BaseUrl = 'http://127.0.0.1:8080/v1';              EnvVar = $null } # local proxy from cursor-openai-api-sync
    'deepseek'        = @{ BaseUrl = 'https://api.deepseek.com';              EnvVar = 'DEEPSEEK_API_KEY' }
    'fireworks'       = @{ BaseUrl = 'https://api.fireworks.ai/inference/v1'; EnvVar = 'FIREWORKS_API_KEY' }
    'gemini'          = @{
        BaseUrl        = 'https://generativelanguage.googleapis.com/v1beta'
        ProfileBaseUrl = 'https://generativelanguage.googleapis.com/v1beta/openai' # Gemini's OpenAI-compat shim
        EnvVar         = 'GEMINI_API_KEY'
        AuthHeader     = 'x-goog-api-key' # the model-list endpoint does not accept Bearer
        Exclude        = 'embedding|aqa|imagen|tts|image'
        Filter         = { param($m) $methods = Get-JsonProperty $m 'supportedGenerationMethods'; $null -ne $methods -and $methods -contains 'generateContent' }
    }
    'inception'       = @{ BaseUrl = 'https://api.inceptionlabs.ai/v1';       EnvVar = 'INCEPTION_API_KEY' }
    'meta'            = @{ BaseUrl = 'https://api.meta.ai/v1';                EnvVar = 'META_AI_API_KEY' }
    'neuralwatt'      = @{ BaseUrl = 'https://api.neuralwatt.com/v1';         EnvVar = 'NEURALWATT_API_KEY' }
    'poe'             = @{ BaseUrl = 'https://api.poe.com/v1';                EnvVar = 'POE_API_KEY' }
    'qwen-token-plan' = @{
        BaseUrl = 'https://token-plan.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1'
        EnvVar  = 'QWEN_TOKEN_PLAN_API_KEY'
        Exclude = 'wan|happyhorse|image|video|t2v|i2v|r2v' # generation models, not useful for a coding agent
    }
    'routera'         = @{ BaseUrl = 'https://api.routera.one/v1';            EnvVar = 'ROUTERA_API_KEY' }
    'sakana'          = @{ BaseUrl = 'https://api.sakana.ai/v1';              EnvVar = 'SAKANA_API_KEY' }
    'venice'          = @{
        BaseUrl = 'https://api.venice.ai/api/v1'
        EnvVar  = 'VENICE_API_KEY'
        Filter  = { param($m) $type = Get-JsonProperty $m 'type'; $spec = Get-JsonProperty $m 'model_spec'; $offline = Get-JsonProperty $spec 'offline'; (-not $type -or $type -eq 'text') -and $offline -ne $true }
    }
    'zai-coding'      = @{ BaseUrl = 'https://api.z.ai/api/coding/paas/v4';   EnvVar = 'ZAI_API_KEY' }
}

# --- Helpers ---------------------------------------------------------------
function Get-JsonProperty
{
    param($Object, [string]$Name)
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    $property.Value
}

function ConvertTo-PlainJson([string]$Text)
{
    # wfx config files may contain // and /* */ comments and trailing commas;
    # ConvertFrom-Json accepts neither, so strip them with a string-aware scanner.
    $builder = [System.Text.StringBuilder]::new($Text.Length)
    $inString = $false
    $escaped = $false
    $i = 0
    while ($i -lt $Text.Length)
    {
        $ch = $Text[$i]
        if ($inString)
        {
            [void]$builder.Append($ch)
            if ($escaped) { $escaped = $false }
            elseif ($ch -eq '\') { $escaped = $true }
            elseif ($ch -eq '"') { $inString = $false }
            $i++
            continue
        }

        if ($ch -eq '"') { $inString = $true; [void]$builder.Append($ch); $i++; continue }

        if ($ch -eq '/' -and $i + 1 -lt $Text.Length -and $Text[$i + 1] -eq '/')
        {
            while ($i -lt $Text.Length -and $Text[$i] -ne "`n") { $i++ }
            continue
        }

        if ($ch -eq '/' -and $i + 1 -lt $Text.Length -and $Text[$i + 1] -eq '*')
        {
            $i += 2
            while ($i + 1 -lt $Text.Length -and -not ($Text[$i] -eq '*' -and $Text[$i + 1] -eq '/')) { $i++ }
            $i += 2
            continue
        }

        if ($ch -eq ',')
        {
            $j = $i + 1
            while ($j -lt $Text.Length -and [char]::IsWhiteSpace($Text[$j])) { $j++ }
            if ($j -lt $Text.Length -and ($Text[$j] -eq '}' -or $Text[$j] -eq ']')) { $i++; continue }
        }

        [void]$builder.Append($ch)
        $i++
    }

    $builder.ToString()
}

function Read-WfxConfig([string]$Path)
{
    if (-not (Test-Path $Path)) { return [ordered]@{} }
    $parsed = (ConvertTo-PlainJson (Get-Content $Path -Raw)) | ConvertFrom-Json -AsHashtable
    if ($null -eq $parsed) { return [ordered]@{} }
    if ($parsed -isnot [System.Collections.Specialized.OrderedDictionary] -and $parsed -isnot [hashtable])
    {
        throw "Configuration file must contain a JSON object: $Path"
    }

    $parsed
}

# Unbound [string] parameters surface as empty strings in pwsh, not $null; treat both as unset.
function Resolve-Setting([string]$Override, $Default)
{
    if ([string]::IsNullOrWhiteSpace($Override)) { $Default } else { $Override }
}

# --- Main ------------------------------------------------------------------
if ($ListProviders)
{
    $script:ProviderRegistry.GetEnumerator() | ForEach-Object {
        [pscustomobject]@{
            Provider = $_.Key
            BaseUrl  = $_.Value.BaseUrl
            EnvVar   = $_.Value['EnvVar'] ?? '(none)'
        }
    } | Format-Table -AutoSize | Out-String | Write-Host
    return
}

if ([string]::IsNullOrWhiteSpace($Provider))
{
    throw "Provider is required. Use -ListProviders to see the registry."
}

$entry = $script:ProviderRegistry[$Provider]
if ($null -eq $entry)
{
    $known = $script:ProviderRegistry.Keys -join ', '
    throw "Unknown provider '$Provider'. Known providers: $known."
}

$resolvedBaseUrl  = Resolve-Setting $BaseUrl $entry['BaseUrl']
$resolvedEndpoint = Resolve-Setting $ModelsEndpoint "$($entry['BaseUrl'])/models"
$resolvedEnvVar   = Resolve-Setting $EnvVar $entry['EnvVar']
$resolvedPrefix   = Resolve-Setting $Prefix $Provider
$profileBaseUrl   = $entry['ProfileBaseUrl'] ?? $resolvedBaseUrl

$headers = @{}
if ($resolvedEnvVar)
{
    $token = [Environment]::GetEnvironmentVariable($resolvedEnvVar)
    if ([string]::IsNullOrEmpty($token))
    {
        throw "$resolvedEnvVar is not set. Set it, or pass -EnvVar to name a different variable."
    }

    $authHeader = $entry['AuthHeader'] ?? 'Authorization'
    $headers[$authHeader] = if ($authHeader -eq 'Authorization') { "Bearer $token" } else { $token }
}

Write-Host "Fetching $resolvedEndpoint ..."
$response = Invoke-RestMethod -Uri $resolvedEndpoint -Headers $headers -Method Get

# OpenAI shape: { data: [ { id, ... } ] }; Gemini shape: { models: [ { name: 'models/x', ... } ] }
$rawModels = if (Get-JsonProperty $response 'data') { $response.data }
             elseif (Get-JsonProperty $response 'models') { $response.models }
             else { throw 'Response included neither a data nor a models array.' }

$modelIds = foreach ($model in $rawModels)
{
    $rawId = Get-JsonProperty $model 'id'
    if ($null -eq $rawId) { $rawId = (Get-JsonProperty $model 'name') -replace '^models/', '' }
    if ([string]::IsNullOrWhiteSpace($rawId)) { continue }
    $id = [string]$rawId
    if ($entry['Filter'] -and -not (& $entry['Filter'] $model)) { continue }
    if ($entry['Exclude'] -and $id -match $entry['Exclude']) { continue }
    if ($Include -and -not ($Include | Where-Object { $id -match $_ })) { continue }
    if ($Exclude -and ($Exclude | Where-Object { $id -match $_ })) { continue }
    $id
}

$modelIds = @($modelIds | Sort-Object -Unique)
if ($modelIds.Count -eq 0)
{
    throw "The catalog fetch for '$Provider' produced zero usable models; refusing to touch $ConfigPath."
}

Write-Host "Catalog: $($modelIds.Count) model(s) for provider '$Provider'."

$config = Read-WfxConfig $ConfigPath
if (-not $config.Contains('profiles') -or $null -eq $config['profiles'])
{
    $config['profiles'] = [ordered]@{}
}
elseif ($config['profiles'] -isnot [System.Collections.Specialized.OrderedDictionary] -and
        $config['profiles'] -isnot [hashtable])
{
    throw "Configuration key 'profiles' must be an object: $ConfigPath"
}

$profiles = $config['profiles']
$managed = @($profiles.Keys | Where-Object { $_.StartsWith("$resolvedPrefix/", [StringComparison]::OrdinalIgnoreCase) })
$desired = [ordered]@{}
foreach ($modelId in $modelIds)
{
    $name = "$resolvedPrefix/$modelId"
    $existing = $profiles[$name]
    $profile = if ($existing -is [System.Collections.Specialized.OrderedDictionary] -or $existing -is [hashtable])
    {
        $existing
    }
    else
    {
        [ordered]@{}
    }

    if (-not $PreserveRouting -or -not $profile.Contains('provider')) { $profile['provider'] = $Provider }
    if (-not $PreserveRouting -or -not $profile.Contains('base_url')) { $profile['base_url'] = $profileBaseUrl }
    $profile['model'] = $modelId
    $desired[$name] = $profile
}

$added = @($desired.Keys | Where-Object { $_ -notin $managed })
$removed = @($managed | Where-Object { -not $desired.Contains($_) })

$changed = $added.Count -gt 0 -or $removed.Count -gt 0
if (-not $changed)
{
    foreach ($name in $desired.Keys)
    {
        $existing = $profiles[$name]
        if ($existing['provider'] -ne $desired[$name]['provider'] -or
            $existing['base_url'] -ne $desired[$name]['base_url'] -or
            $existing['model'] -ne $desired[$name]['model'])
        {
            $changed = $true
            break
        }
    }
}

Write-Host "Plan: $($added.Count) added, $($desired.Count - $added.Count) updated, $($removed.Count) removed (managed namespace '$resolvedPrefix/*')."

if ($DryRun)
{
    Write-Host ''
    Write-Host 'Dry run; no changes written.'
    foreach ($name in $added) { Write-Host "  + $name" }
    foreach ($name in $removed) { Write-Host "  - $name" }
    return
}

if (-not $changed)
{
    Write-Host "Profiles already up to date; $ConfigPath left untouched."
    return
}

if ($PSCmdlet.ShouldProcess($ConfigPath, "sync $($desired.Count) profile(s)"))
{
    foreach ($name in $desired.Keys) { $profiles[$name] = $desired[$name] }
    foreach ($name in $removed) { $profiles.Remove($name) }

    $directory = Split-Path $ConfigPath -Parent
    if ($directory) { [void](New-Item -ItemType Directory -Path $directory -Force) }
    if (Test-Path $ConfigPath)
    {
        Copy-Item $ConfigPath "$ConfigPath.bak" -Force
    }

    ($config | ConvertTo-Json -Depth 32) | Set-Content $ConfigPath -Encoding utf8
    Write-Host "Wrote $($desired.Count) profile(s) to $ConfigPath (backup: $ConfigPath.bak)"
    if ($removed.Count -gt 0) { Write-Host "Removed $($removed.Count) stale profile(s)." }
    if ($resolvedEnvVar)
    {
        Write-Host "Reminder: profiles carry no secrets. Set WFX_API_KEY (or add ""api_key"" to a profile) before running wfx."
    }

    Write-Host "Validate with: wfx config --profile $($desired.Keys | Select-Object -First 1)"
}
