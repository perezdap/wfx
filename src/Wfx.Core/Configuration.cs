using System.Text.Json;

namespace Wfx.Core;

public sealed record WfxSettingsLayer
{
    public string? Provider { get; init; }

    public string? Protocol { get; init; }

    public string? BaseUrl { get; init; }

    public string? ApiKey { get; init; }

    public string? Model { get; init; }

    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    public int? TimeoutSeconds { get; init; }

    public int? MaxIterations { get; init; }

    public ApprovalMode? Approval { get; init; }

    public string? Profile { get; init; }

    public IReadOnlyDictionary<string, WfxSettingsLayer>? Profiles { get; init; }

    /// <summary>
    /// MCP servers this layer declared. Only the user layer may supply them; a project layer
    /// containing this key is rejected when the configuration loads.
    /// </summary>
    public IReadOnlyDictionary<string, McpServerSettings>? McpServers { get; init; }
}

public sealed class UndefinedProfileException : InvalidOperationException
{
    public UndefinedProfileException(string profileName, string message)
        : base(message)
    {
        ProfileName = profileName;
    }

    public string ProfileName { get; }
}

public sealed record ConfiguredModel(string Profile, string Provider, string Model);

/// <summary>
/// One configuration layer's contribution to the effective settings: the layer name
/// (<c>defaults</c>, <c>user</c>, <c>project</c>, <c>environment</c>, or <c>cli</c>), the file
/// path the layer was read from when it came from a file, and the setting keys the layer
/// supplied to the effective result.
/// </summary>
public sealed record ConfigurationSource(string Layer, string? Path, IReadOnlyList<string> Keys);

/// <summary>
/// One entry of the configured-models listing: a profile carrying a model key with enough
/// endpoint detail to list it programmatically. Entries that fail to resolve carry the
/// resolution error and null endpoint fields; presentation decides whether to show them.
/// </summary>
public sealed record ModelListingEntry(
    string Name,
    string Provider,
    string? Protocol,
    Uri? BaseUri,
    string Model,
    bool HasCredentials,
    string? Error);

internal sealed record ConfiguredModelResolution(
    ConfiguredModel Model,
    WfxSettings? Settings,
    string? Error);

public sealed record WfxSettings(
    string Provider,
    string Protocol,
    Uri BaseUri,
    string? ApiKey,
    string Model,
    IReadOnlyDictionary<string, string> Headers,
    TimeSpan Timeout,
    int MaxIterations,
    ApprovalMode Approval)
{
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public string? Profile { get; init; }

    public IReadOnlyList<ConfiguredModel> ConfiguredModels { get; init; } = [];

    /// <summary>Per-layer provenance of the effective settings, lowest precedence first.</summary>
    public IReadOnlyList<ConfigurationSource> Sources { get; init; } = [];

    /// <summary>Every configured profile carrying a model key, with endpoint detail for listing.</summary>
    public IReadOnlyList<ModelListingEntry> ModelListing { get; init; } = [];

    /// <summary>User-configured MCP stdio servers; empty when none are configured.</summary>
    public IReadOnlyDictionary<string, McpServerSettings> McpServers { get; init; } =
        new Dictionary<string, McpServerSettings>(StringComparer.OrdinalIgnoreCase);

    internal IReadOnlyList<ConfiguredModelResolution> ConfiguredModelResolutions { get; init; } = [];
}

public static class WfxConfiguration
{
    private const string DefaultProtocol = "chat_completions";

    private static readonly string[] KnownProtocols = [DefaultProtocol, "responses", "anthropic_messages"];

    public static WfxSettings Load(
        string workspaceRoot,
        WfxSettingsLayer? cli = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        string? userProfile = null)
    {
        userProfile ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var userConfig = Path.GetFullPath(Path.Combine(userProfile, ".wfx", "config.json"));
        var projectConfig = Path.Combine(Path.GetFullPath(workspaceRoot), ".wfx", "config.json");
        // From the profile root, the user and project config are the same file.
        // Loading it once (as the project layer) yields an identical merge and
        // avoids a spurious credential-suppression warning for a file that
        // suppresses nothing.
        var sameConfigFile = string.Equals(userConfig, projectConfig, StringComparison.OrdinalIgnoreCase);
        WfxSettingsLayer? userLayer = null;
        if (!sameConfigFile && File.Exists(userConfig))
        {
            userLayer = ReadFile(userConfig);
        }

        WfxSettingsLayer? projectLayer = null;
        if (File.Exists(projectConfig))
        {
            projectLayer = ReadFile(projectConfig);
        }

        // A cloned repository must not be able to launch executable MCP servers, so the key
        // is refused in project config outright. When both files are the same file the layer
        // is the user's own configuration and the key stays allowed.
        if (!sameConfigFile)
        {
            RejectProjectMcpServers(projectLayer, projectConfig);
        }

        var environmentLayer = FromEnvironment(environment);
        var profile = cli?.Profile ?? environmentLayer.Profile ?? projectLayer?.Profile ?? userLayer?.Profile;
        var settings = ResolveSettings(userLayer, projectLayer, environmentLayer, cli, profile, environment, sameConfigFile);
        var configuredModels = BuildConfiguredModels(userLayer, projectLayer, environmentLayer, cli, environment);
        var sources = ComputeSources(
            userLayer,
            userLayer is null ? null : userConfig,
            projectLayer,
            projectLayer is null ? null : projectConfig,
            environmentLayer,
            cli,
            profile,
            environment,
            settings);
        return settings with
        {
            ConfiguredModels = configuredModels.Select(static resolution => resolution.Model).ToArray(),
            ConfiguredModelResolutions = configuredModels,
            ModelListing = configuredModels.Select(static resolution => new ModelListingEntry(
                resolution.Model.Profile,
                resolution.Model.Provider,
                resolution.Settings?.Protocol,
                resolution.Settings?.BaseUri,
                resolution.Model.Model,
                resolution.Settings is not null && HasCredentials(resolution.Settings),
                resolution.Error)).ToArray(),
            Sources = sources
        };
    }

    /// <summary>
    /// A project-scoped base_url pins the endpoint, so credentials from broader layers are
    /// suppressed unless the project or CLI scope supplies its own. Both effective resolution
    /// and provenance attribution depend on the same boundary, computed once here.
    /// </summary>
    private static bool ProjectControlsBaseUrl(
        WfxSettingsLayer? projectLayer,
        WfxSettingsLayer environmentLayer,
        WfxSettingsLayer? cli) =>
        !string.IsNullOrWhiteSpace(projectLayer?.BaseUrl) &&
        string.IsNullOrWhiteSpace(environmentLayer.BaseUrl) &&
        string.IsNullOrWhiteSpace(cli?.BaseUrl);

    private static bool HasCredentials(WfxSettings settings) =>
        settings.ApiKey is not null ||
        settings.Headers.Values.Any(static value => !string.IsNullOrEmpty(value));

    private static readonly string[] SourceKeyOrder =
    [
        "provider", "protocol", "base_url", "api_key", "model", "headers",
        "timeout_seconds", "max_iterations", "approval", "profile", "mcp_servers"
    ];

    // The keys whose winning layer is decided by Merge's plain non-null override. This table
    // mirrors Merge: adding a setting there means adding it here (and to SourceKeyOrder).
    private static readonly (string Key, Func<WfxSettingsLayer, bool> IsSet)[] MergeDecidedKeys =
    [
        ("provider", static layer => layer.Provider is not null),
        ("protocol", static layer => layer.Protocol is not null),
        ("base_url", static layer => layer.BaseUrl is not null),
        ("model", static layer => layer.Model is not null),
        ("timeout_seconds", static layer => layer.TimeoutSeconds is not null),
        ("max_iterations", static layer => layer.MaxIterations is not null),
        ("approval", static layer => layer.Approval is not null),
        ("mcp_servers", static layer => layer.McpServers is not null)
    ];

    private static IReadOnlyList<ConfigurationSource> ComputeSources(
        WfxSettingsLayer? userLayer,
        string? userPath,
        WfxSettingsLayer? projectLayer,
        string? projectPath,
        WfxSettingsLayer environmentLayer,
        WfxSettingsLayer? cli,
        string? profile,
        IReadOnlyDictionary<string, string?>? environment,
        WfxSettings settings)
    {
        var profileWinner = cli?.Profile is not null ? "cli"
            : environmentLayer.Profile is not null ? "environment"
            : projectLayer?.Profile is not null ? "project"
            : userLayer?.Profile is not null ? "user"
            : null;

        if (profile is not null)
        {
            (userLayer, projectLayer) = ExpandProfile(profile, userLayer, projectLayer);
        }

        var layers = new List<(string Name, string? Path, WfxSettingsLayer Layer)>
        {
            ("defaults", null, Defaults)
        };
        if (userLayer is not null)
        {
            layers.Add(("user", userPath, userLayer));
        }

        if (projectLayer is not null)
        {
            layers.Add(("project", projectPath, projectLayer));
        }

        layers.Add(("environment", null, environmentLayer));
        if (cli is not null)
        {
            layers.Add(("cli", null, cli));
        }

        var projectControlsBaseUrl = ProjectControlsBaseUrl(projectLayer, environmentLayer, cli);

        var winners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, _, layer) in layers)
        {
            foreach (var (key, isSet) in MergeDecidedKeys)
            {
                if (isSet(layer))
                {
                    winners[key] = name;
                }
            }
        }

        if (settings.ApiKey is not null)
        {
            if (projectControlsBaseUrl)
            {
                if (!string.IsNullOrWhiteSpace(cli?.ApiKey))
                {
                    winners["api_key"] = "cli";
                }
                else if (!string.IsNullOrWhiteSpace(projectLayer?.ApiKey))
                {
                    winners["api_key"] = "project";
                }
            }
            else
            {
                foreach (var (name, _, layer) in layers)
                {
                    if (!string.IsNullOrWhiteSpace(layer.ApiKey))
                    {
                        winners["api_key"] = name;
                    }
                }

                // No layer supplied the key, so it came from the provider-specific
                // credential variable read straight from the environment.
                winners.TryAdd("api_key", "environment");
            }
        }

        if (settings.Headers.Count > 0)
        {
            if (projectControlsBaseUrl)
            {
                if (cli?.Headers is not null)
                {
                    winners["headers"] = "cli";
                }
                else if (projectLayer?.Headers is not null)
                {
                    winners["headers"] = "project";
                }
            }
            else
            {
                foreach (var (name, _, layer) in layers)
                {
                    if (layer.Headers is not null)
                    {
                        winners["headers"] = name;
                    }
                }
            }
        }

        if (profileWinner is not null)
        {
            winners["profile"] = profileWinner;
        }

        var sources = new List<ConfigurationSource>();
        foreach (var (name, path, _) in layers)
        {
            var keys = SourceKeyOrder.Where(key => winners.TryGetValue(key, out var winner) && winner == name)
                .ToArray();
            if (keys.Length > 0)
            {
                sources.Add(new ConfigurationSource(name, path, keys));
            }
        }

        return sources;
    }

    private static WfxSettings ResolveSettings(
        WfxSettingsLayer? userLayer,
        WfxSettingsLayer? projectLayer,
        WfxSettingsLayer environmentLayer,
        WfxSettingsLayer? cli,
        string? profile,
        IReadOnlyDictionary<string, string?>? environment,
        bool projectLayerIsUserConfig = false)
    {
        if (profile is not null)
        {
            (userLayer, projectLayer) = ExpandProfile(profile, userLayer, projectLayer);
        }

        var layers = new List<WfxSettingsLayer> { Defaults };
        if (userLayer is not null)
        {
            layers.Add(userLayer);
        }

        if (projectLayer is not null)
        {
            layers.Add(projectLayer);
        }

        layers.Add(environmentLayer);
        if (cli is not null)
        {
            layers.Add(cli);
        }

        var merged = Merge(layers);
        var provider = merged.Provider ?? "openai";
        var protocol = ResolveProtocol(merged.Protocol);
        ValidateProtocol(protocol);
        var credentialEnvironmentVariable = CredentialEnvironmentVariable(provider);
        var baseUrl = merged.BaseUrl ?? TryProviderDefaultBaseUrl(provider) ?? ProtocolDefaultBaseUrl(protocol)
            ?? throw new InvalidOperationException($"Provider '{provider}' requires an explicit base_url.");
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("The configured base_url must be an absolute HTTP or HTTPS URL.");
        }

        var projectControlsBaseUrl = ProjectControlsBaseUrl(projectLayer, environmentLayer, cli);
        var apiKey = projectControlsBaseUrl
            ? cli?.ApiKey ?? projectLayer?.ApiKey
            : merged.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey) && !projectControlsBaseUrl)
        {
            apiKey = GetEnvironment(environment, credentialEnvironmentVariable);
        }

        var headers = projectControlsBaseUrl
            ? cli?.Headers ?? projectLayer?.Headers ?? new Dictionary<string, string>()
            : merged.Headers ?? new Dictionary<string, string>();

        var warnings = new List<string>();
        if (projectControlsBaseUrl)
        {
            var suppressedCredential = !string.IsNullOrWhiteSpace(userLayer?.ApiKey) ||
                !string.IsNullOrWhiteSpace(environmentLayer.ApiKey) ||
                !string.IsNullOrWhiteSpace(GetEnvironment(environment, credentialEnvironmentVariable));
            var suppressedHeaders = (userLayer?.Headers is not null || environmentLayer.Headers is not null) &&
                projectLayer?.Headers is null &&
                cli?.Headers is null;
            if (suppressedCredential || suppressedHeaders)
            {
                warnings.Add("Project base_url suppressed user or environment credentials. Configure the endpoint at user/environment/CLI scope, or set credentials in project/CLI scope to use them with this endpoint.");
            }
        }

        return new WfxSettings(
            provider,
            protocol,
            baseUri,
            string.IsNullOrWhiteSpace(apiKey) ? null : apiKey,
            merged.Model ?? string.Empty,
            headers,
            TimeSpan.FromSeconds(Math.Clamp(merged.TimeoutSeconds ?? 300, 1, 3600)),
            Math.Clamp(merged.MaxIterations ?? 24, 1, 100),
            merged.Approval ?? ApprovalMode.Always)
        {
            Warnings = warnings,
            Profile = profile,
            // MCP servers come from the user layer only; the single-file case loads the
            // user's configuration as the project layer.
            McpServers = (projectLayerIsUserConfig ? projectLayer : userLayer)?.McpServers ??
                new Dictionary<string, McpServerSettings>(StringComparer.OrdinalIgnoreCase)
        };
    }

    public static WfxSettingsLayer ParseModelShorthand(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var separator = value.IndexOf('/');
        if (separator > 0)
        {
            var prefix = value[..separator];
            if (prefix.Equals("openrouter", StringComparison.OrdinalIgnoreCase))
            {
                return new WfxSettingsLayer { Provider = "openrouter", Model = value[(separator + 1)..] };
            }
        }

        return new WfxSettingsLayer { Model = value };
    }

    public static bool TryParseApprovalMode(string? value, out ApprovalMode mode)
    {
        switch (value?.ToLowerInvariant())
        {
            case "always":
                mode = ApprovalMode.Always;
                return true;
            case "workspace":
                mode = ApprovalMode.Workspace;
                return true;
            case "never":
                mode = ApprovalMode.Never;
                return true;
            case "yolo":
                mode = ApprovalMode.AllowAll;
                return true;
            default:
                mode = default;
                return false;
        }
    }

    public static string FormatApprovalMode(ApprovalMode mode) => mode switch
    {
        ApprovalMode.Always => "always",
        ApprovalMode.Workspace => "workspace",
        ApprovalMode.Never => "never",
        ApprovalMode.AllowAll => "yolo",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown approval mode.")
    };

    public static WfxSettingsLayer ReadFile(string path)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
        }
        catch (JsonException exception)
        {
            // A file that cannot be parsed is a configuration error, same as a file with the
            // wrong shape; callers map InvalidOperationException to the config-error exit code.
            throw new InvalidOperationException(
                $"Configuration file is not valid JSON: {path}: {exception.Message}",
                exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException($"Configuration file must contain a JSON object: {path}");
            }

            IReadOnlyDictionary<string, WfxSettingsLayer>? profiles = null;
            if (root.TryGetProperty("profiles", out var profilesElement))
            {
                profiles = ParseProfiles(profilesElement, path);
            }

            var layer = ParseLayer(root, path);
            return layer with { Profile = GetString(root, "profile"), Profiles = profiles };
        }
    }

    private static Dictionary<string, WfxSettingsLayer> ParseProfiles(JsonElement profilesElement, string path)
    {
        if (profilesElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"Configuration profiles must be an object: {path}");
        }

        var profiles = new Dictionary<string, WfxSettingsLayer>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in profilesElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException($"Configuration profile '{property.Name}' must be an object: {path}");
            }

            if (property.Value.TryGetProperty("profile", out _))
            {
                throw new InvalidOperationException($"Configuration profile '{property.Name}' cannot contain a 'profile' key: {path}");
            }

            if (property.Value.TryGetProperty("profiles", out _))
            {
                throw new InvalidOperationException($"Configuration profile '{property.Name}' cannot contain a nested 'profiles' map: {path}");
            }

            if (!profiles.TryAdd(property.Name, ParseLayer(property.Value, path)))
            {
                throw new InvalidOperationException($"Configuration defines a duplicate profile '{property.Name}': {path}");
            }
        }

        return profiles;
    }

    private static WfxSettingsLayer ParseLayer(JsonElement root, string path)
    {
        Dictionary<string, string>? headers = null;
        if (root.TryGetProperty("headers", out var headerElement))
        {
            if (headerElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException($"Configuration headers must be an object: {path}");
            }

            headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in headerElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidOperationException($"Configuration header '{property.Name}' must be a string: {path}");
                }

                headers[property.Name] = property.Value.GetString()!;
            }
        }

        IReadOnlyDictionary<string, McpServerSettings>? mcpServers = null;
        if (root.TryGetProperty("mcp_servers", out var mcpElement))
        {
            mcpServers = ParseMcpServers(mcpElement, path);
        }

        return new WfxSettingsLayer
        {
            Provider = GetString(root, "provider"),
            Protocol = GetString(root, "protocol"),
            BaseUrl = GetString(root, "base_url"),
            ApiKey = GetString(root, "api_key"),
            Model = GetString(root, "model"),
            Headers = headers,
            TimeoutSeconds = GetInteger(root, "timeout_seconds"),
            MaxIterations = GetInteger(root, "max_iterations"),
            Approval = GetApproval(root, "approval"),
            McpServers = mcpServers
        };
    }

    private static IReadOnlyDictionary<string, McpServerSettings> ParseMcpServers(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"Configuration mcp_servers must be an object: {path}");
        }

        var servers = new Dictionary<string, McpServerSettings>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException($"MCP server '{property.Name}' must be an object: {path}");
            }

            var command = GetString(property.Value, "command");
            if (string.IsNullOrWhiteSpace(command))
            {
                throw new InvalidOperationException($"MCP server '{property.Name}' must define a non-empty 'command': {path}");
            }

            var arguments = new List<string>();
            if (property.Value.TryGetProperty("args", out var argsElement))
            {
                if (argsElement.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidOperationException($"MCP server '{property.Name}' 'args' must be an array of strings: {path}");
                }

                foreach (var item in argsElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                    {
                        throw new InvalidOperationException($"MCP server '{property.Name}' 'args' must be an array of strings: {path}");
                    }

                    arguments.Add(item.GetString()!);
                }
            }

            var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (property.Value.TryGetProperty("env", out var envElement))
            {
                if (envElement.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException($"MCP server '{property.Name}' 'env' must be an object with string values: {path}");
                }

                foreach (var variable in envElement.EnumerateObject())
                {
                    if (variable.Value.ValueKind != JsonValueKind.String)
                    {
                        throw new InvalidOperationException($"MCP server '{property.Name}' environment variable '{variable.Name}' must be a string: {path}");
                    }

                    environment[variable.Name] = variable.Value.GetString()!;
                }
            }

            if (!servers.TryAdd(property.Name, new McpServerSettings(command, arguments, environment)))
            {
                throw new InvalidOperationException($"Configuration defines a duplicate MCP server '{property.Name}': {path}");
            }
        }

        return servers;
    }

    private static void RejectProjectMcpServers(WfxSettingsLayer? projectLayer, string path)
    {
        if (projectLayer is null)
        {
            return;
        }

        var profileSuppliesServers = projectLayer.Profiles is not null &&
            projectLayer.Profiles.Values.Any(static profile => profile.McpServers is not null);
        if (projectLayer.McpServers is null && !profileSuppliesServers)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Configuration 'mcp_servers' is only allowed in the user configuration; remove 'mcp_servers' from the project configuration: {path}");
    }

    private static WfxSettingsLayer Defaults => new()
    {
        Provider = "openai",
        TimeoutSeconds = 300,
        MaxIterations = 24,
        Approval = ApprovalMode.Always
    };

    private static WfxSettingsLayer FromEnvironment(IReadOnlyDictionary<string, string?>? environment) => new()
    {
        Provider = GetEnvironment(environment, "WFX_PROVIDER"),
        Protocol = GetEnvironment(environment, "WFX_PROTOCOL"),
        BaseUrl = GetEnvironment(environment, "WFX_BASE_URL"),
        ApiKey = GetEnvironment(environment, "WFX_API_KEY"),
        Model = GetEnvironment(environment, "WFX_MODEL"),
        TimeoutSeconds = ParseEnvironmentInteger(environment, "WFX_TIMEOUT_SECONDS"),
        MaxIterations = ParseEnvironmentInteger(environment, "WFX_MAX_ITERATIONS"),
        Approval = ParseApproval(GetEnvironment(environment, "WFX_APPROVAL")),
        Profile = GetEnvironment(environment, "WFX_PROFILE")
    };

    private static (WfxSettingsLayer? User, WfxSettingsLayer? Project) ExpandProfile(
        string name,
        WfxSettingsLayer? userLayer,
        WfxSettingsLayer? projectLayer)
    {
        var userProfile = GetProfile(userLayer, name);
        var projectProfile = GetProfile(projectLayer, name);
        if (userProfile is null && projectProfile is null)
        {
            throw UndefinedProfile(name, userLayer, projectLayer);
        }

        return (ExpandInPlace(userLayer, userProfile), ExpandInPlace(projectLayer, projectProfile));
    }

    private static WfxSettingsLayer? GetProfile(WfxSettingsLayer? layer, string name) =>
        layer?.Profiles is not null && layer.Profiles.TryGetValue(name, out var profile)
            ? profile
            : null;

    private static WfxSettingsLayer? ExpandInPlace(WfxSettingsLayer? layer, WfxSettingsLayer? profile)
    {
        if (layer is null)
        {
            return null;
        }

        var baseLayer = layer with { Profiles = null };
        return profile is null ? baseLayer : Merge([baseLayer, profile]);
    }

    private static InvalidOperationException UndefinedProfile(
        string name,
        WfxSettingsLayer? userLayer,
        WfxSettingsLayer? projectLayer)
    {
        var userProfiles = ProfileNames(userLayer);
        var projectProfiles = ProfileNames(projectLayer);
        if (userProfiles.Count == 0 && projectProfiles.Count == 0)
        {
            return new UndefinedProfileException(
                name,
                $"Profile '{name}' is not defined; no profiles exist in the user or project configuration files.");
        }

        return new UndefinedProfileException(
            name,
            $"Profile '{name}' is not defined. Available profiles — user: {FormatProfileNames(userProfiles)}; project: {FormatProfileNames(projectProfiles)}.");
    }

    private static IReadOnlyList<string> ProfileNames(WfxSettingsLayer? layer) =>
        layer?.Profiles?.Keys.ToArray() ?? [];

    private static string FormatProfileNames(IReadOnlyList<string> names) =>
        names.Count == 0 ? "(none)" : string.Join(", ", names);

    private static IReadOnlyList<ConfiguredModelResolution> BuildConfiguredModels(
        WfxSettingsLayer? userLayer,
        WfxSettingsLayer? projectLayer,
        WfxSettingsLayer environmentLayer,
        WfxSettingsLayer? cli,
        IReadOnlyDictionary<string, string?>? environment)
    {
        var names = ProfileNames(userLayer)
            .Concat(ProfileNames(projectLayer))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase);
        var models = new List<ConfiguredModelResolution>();
        foreach (var name in names)
        {
            var layers = new List<WfxSettingsLayer>();
            if (GetProfile(userLayer, name) is { } userProfile)
            {
                layers.Add(userProfile);
            }

            if (GetProfile(projectLayer, name) is { } projectProfile)
            {
                layers.Add(projectProfile);
            }

            var profile = Merge(layers);
            if (string.IsNullOrWhiteSpace(profile.Model))
            {
                continue;
            }

            var candidateCli = (cli ?? new WfxSettingsLayer()) with { Profile = name, Model = null };
            try
            {
                var settings = ResolveSettings(
                    userLayer,
                    projectLayer,
                    environmentLayer,
                    candidateCli,
                    name,
                    environment) with
                {
                    Model = profile.Model
                };
                models.Add(new ConfiguredModelResolution(
                    new ConfiguredModel(name, settings.Provider, profile.Model),
                    settings,
                    null));
            }
            catch (InvalidOperationException exception)
            {
                models.Add(new ConfiguredModelResolution(
                    new ConfiguredModel(
                        name,
                        ResolveProvider(name, userLayer, projectLayer, environmentLayer, candidateCli),
                        profile.Model),
                    null,
                    exception.Message));
            }
        }

        return models;
    }

    private static string ResolveProvider(
        string profile,
        WfxSettingsLayer? userLayer,
        WfxSettingsLayer? projectLayer,
        WfxSettingsLayer environmentLayer,
        WfxSettingsLayer cli)
    {
        (userLayer, projectLayer) = ExpandProfile(profile, userLayer, projectLayer);
        var layers = new List<WfxSettingsLayer> { Defaults };
        if (userLayer is not null)
        {
            layers.Add(userLayer);
        }

        if (projectLayer is not null)
        {
            layers.Add(projectLayer);
        }

        layers.Add(environmentLayer);
        layers.Add(cli);
        return Merge(layers).Provider ?? "openai";
    }

    private static WfxSettingsLayer Merge(IEnumerable<WfxSettingsLayer> layers)
    {
        var result = new WfxSettingsLayer();
        foreach (var layer in layers)
        {
            result = new WfxSettingsLayer
            {
                Provider = layer.Provider ?? result.Provider,
                Protocol = layer.Protocol ?? result.Protocol,
                BaseUrl = layer.BaseUrl ?? result.BaseUrl,
                ApiKey = layer.ApiKey ?? result.ApiKey,
                Model = layer.Model ?? result.Model,
                Headers = layer.Headers ?? result.Headers,
                TimeoutSeconds = layer.TimeoutSeconds ?? result.TimeoutSeconds,
                MaxIterations = layer.MaxIterations ?? result.MaxIterations,
                Approval = layer.Approval ?? result.Approval,
                McpServers = layer.McpServers ?? result.McpServers
            };
        }

        return result;
    }

    private static string ResolveProtocol(string? protocol) =>
        string.IsNullOrWhiteSpace(protocol) ? DefaultProtocol : protocol;

    private static void ValidateProtocol(string protocol)
    {
        if (protocol.Equals("anthropic_messages", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Protocol 'anthropic_messages' is not implemented yet.");
        }

        if (!KnownProtocols.Any(known => known.Equals(protocol, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Protocol '{protocol}' is not supported. Valid values are: {string.Join(", ", KnownProtocols)}.");
        }
    }

    private static string? TryProviderDefaultBaseUrl(string provider) => provider.ToLowerInvariant() switch
    {
        "openai" => "https://api.openai.com/v1",
        "openrouter" => "https://openrouter.ai/api/v1",
        "local" => "http://localhost:1234/v1",
        "anthropic" => "https://api.anthropic.com/v1",
        _ => null
    };

    private static string? ProtocolDefaultBaseUrl(string protocol) => protocol.ToLowerInvariant() switch
    {
        "responses" => "https://api.openai.com/v1",
        _ => null
    };

    private static string CredentialEnvironmentVariable(string provider) =>
        provider.ToLowerInvariant() switch
        {
            "openrouter" => "OPENROUTER_API_KEY",
            "anthropic" => "ANTHROPIC_API_KEY",
            _ => "OPENAI_API_KEY"
        };

    private static string? GetString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException($"Configuration value '{name}' must be a string.");
        }

        return value.GetString();
    }

    private static int? GetInteger(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (!value.TryGetInt32(out var result))
        {
            throw new InvalidOperationException($"Configuration value '{name}' must be an integer.");
        }

        return result;
    }

    private static ApprovalMode? GetApproval(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
            ? ParseApproval(value.GetString())
            : null;

    private static ApprovalMode? ParseApproval(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        return TryParseApprovalMode(value, out var mode)
            ? mode
            : throw new InvalidOperationException("Approval must be always, workspace, never, or yolo.");
    }

    private static int? ParseEnvironmentInteger(IReadOnlyDictionary<string, string?>? environment, string name)
    {
        var value = GetEnvironment(environment, name);
        if (value is null)
        {
            return null;
        }

        return int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var result)
            ? result
            : throw new InvalidOperationException($"Environment variable {name} must be an integer.");
    }

    private static string? GetEnvironment(IReadOnlyDictionary<string, string?>? environment, string name)
    {
        if (environment is not null)
        {
            return environment.TryGetValue(name, out var value) ? value : null;
        }

        return Environment.GetEnvironmentVariable(name);
    }
}
