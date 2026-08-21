using System.Text.Json;

namespace Wfx.Core;

public sealed record WfxSettingsLayer
{
    public string? Provider { get; init; }

    public string? BaseUrl { get; init; }

    public string? ApiKey { get; init; }

    public string? Model { get; init; }

    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    public int? TimeoutSeconds { get; init; }

    public int? MaxIterations { get; init; }

    public ApprovalMode? Approval { get; init; }

    public string? Profile { get; init; }

    public IReadOnlyDictionary<string, WfxSettingsLayer>? Profiles { get; init; }
}

public sealed record WfxSettings(
    string Provider,
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
}

public static class WfxConfiguration
{
    public static WfxSettings Load(
        string workspaceRoot,
        WfxSettingsLayer? cli = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        string? userProfile = null)
    {
        var layers = new List<WfxSettingsLayer> { Defaults };
        userProfile ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var userConfig = Path.Combine(userProfile, ".wfx", "config.json");
        var projectConfig = Path.Combine(Path.GetFullPath(workspaceRoot), ".wfx", "config.json");
        // From the profile root, the user and project config are the same file.
        // Loading it once (as the project layer) yields an identical merge and
        // avoids a spurious credential-suppression warning for a file that
        // suppresses nothing.
        var sameConfigFile = string.Equals(Path.GetFullPath(userConfig), projectConfig, StringComparison.OrdinalIgnoreCase);
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

        var environmentLayer = FromEnvironment(environment);
        var profile = cli?.Profile ?? environmentLayer.Profile ?? projectLayer?.Profile ?? userLayer?.Profile;
        if (profile is not null)
        {
            (userLayer, projectLayer) = ExpandProfile(profile, userLayer, projectLayer);
        }

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
        var baseUrl = merged.BaseUrl ?? ProviderDefaultBaseUrl(provider);
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException("The configured base_url must be an absolute HTTP or HTTPS URL.");
        }

        var projectControlsBaseUrl = !string.IsNullOrWhiteSpace(projectLayer?.BaseUrl) &&
            string.IsNullOrWhiteSpace(environmentLayer.BaseUrl) &&
            string.IsNullOrWhiteSpace(cli?.BaseUrl);
        var apiKey = projectControlsBaseUrl
            ? cli?.ApiKey ?? projectLayer?.ApiKey
            : merged.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey) && !projectControlsBaseUrl)
        {
            apiKey = GetEnvironment(environment, provider.Equals("openrouter", StringComparison.OrdinalIgnoreCase)
                ? "OPENROUTER_API_KEY"
                : "OPENAI_API_KEY");
        }

        var headers = projectControlsBaseUrl
            ? cli?.Headers ?? projectLayer?.Headers ?? new Dictionary<string, string>()
            : merged.Headers ?? new Dictionary<string, string>();

        var warnings = new List<string>();
        if (projectControlsBaseUrl)
        {
            var suppressedCredential = !string.IsNullOrWhiteSpace(userLayer?.ApiKey) ||
                !string.IsNullOrWhiteSpace(environmentLayer.ApiKey) ||
                !string.IsNullOrWhiteSpace(GetEnvironment(environment, provider.Equals("openrouter", StringComparison.OrdinalIgnoreCase)
                    ? "OPENROUTER_API_KEY"
                    : "OPENAI_API_KEY"));
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
            baseUri,
            string.IsNullOrWhiteSpace(apiKey) ? null : apiKey,
            merged.Model ?? string.Empty,
            headers,
            TimeSpan.FromSeconds(Math.Clamp(merged.TimeoutSeconds ?? 300, 1, 3600)),
            Math.Clamp(merged.MaxIterations ?? 24, 1, 100),
            merged.Approval ?? ApprovalMode.Always)
        {
            Warnings = warnings,
            Profile = profile
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

    public static WfxSettingsLayer ReadFile(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });
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

        return new WfxSettingsLayer
        {
            Provider = GetString(root, "provider"),
            BaseUrl = GetString(root, "base_url"),
            ApiKey = GetString(root, "api_key"),
            Model = GetString(root, "model"),
            Headers = headers,
            TimeoutSeconds = GetInteger(root, "timeout_seconds"),
            MaxIterations = GetInteger(root, "max_iterations"),
            Approval = GetApproval(root, "approval")
        };
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
            return new InvalidOperationException(
                $"Profile '{name}' is not defined; no profiles exist in the user or project configuration files.");
        }

        return new InvalidOperationException(
            $"Profile '{name}' is not defined. Available profiles — user: {FormatProfileNames(userProfiles)}; project: {FormatProfileNames(projectProfiles)}.");
    }

    private static IReadOnlyList<string> ProfileNames(WfxSettingsLayer? layer) =>
        layer?.Profiles?.Keys.ToArray() ?? [];

    private static string FormatProfileNames(IReadOnlyList<string> names) =>
        names.Count == 0 ? "(none)" : string.Join(", ", names);

    private static WfxSettingsLayer Merge(IEnumerable<WfxSettingsLayer> layers)
    {
        var result = new WfxSettingsLayer();
        foreach (var layer in layers)
        {
            result = new WfxSettingsLayer
            {
                Provider = layer.Provider ?? result.Provider,
                BaseUrl = layer.BaseUrl ?? result.BaseUrl,
                ApiKey = layer.ApiKey ?? result.ApiKey,
                Model = layer.Model ?? result.Model,
                Headers = layer.Headers ?? result.Headers,
                TimeoutSeconds = layer.TimeoutSeconds ?? result.TimeoutSeconds,
                MaxIterations = layer.MaxIterations ?? result.MaxIterations,
                Approval = layer.Approval ?? result.Approval
            };
        }

        return result;
    }

    private static string ProviderDefaultBaseUrl(string provider) => provider.ToLowerInvariant() switch
    {
        "openai" => "https://api.openai.com/v1",
        "openrouter" => "https://openrouter.ai/api/v1",
        "local" => "http://localhost:1234/v1",
        _ => throw new InvalidOperationException($"Provider '{provider}' requires an explicit base_url.")
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

    private static ApprovalMode? ParseApproval(string? value) => value?.ToLowerInvariant() switch
    {
        null or "" => null,
        "always" => ApprovalMode.Always,
        "workspace" => ApprovalMode.Workspace,
        "never" => ApprovalMode.Never,
        _ => throw new InvalidOperationException("Approval must be always, workspace, or never.")
    };

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
