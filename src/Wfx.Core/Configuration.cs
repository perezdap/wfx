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
}

public sealed record WfxSettings(
    string Provider,
    Uri BaseUri,
    string? ApiKey,
    string Model,
    IReadOnlyDictionary<string, string> Headers,
    TimeSpan Timeout,
    int MaxIterations,
    ApprovalMode Approval);

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
        if (File.Exists(userConfig))
        {
            layers.Add(ReadFile(userConfig));
        }

        if (File.Exists(projectConfig))
        {
            layers.Add(ReadFile(projectConfig));
        }

        layers.Add(FromEnvironment(environment));
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

        var apiKey = merged.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            apiKey = GetEnvironment(environment, provider.Equals("openrouter", StringComparison.OrdinalIgnoreCase)
                ? "OPENROUTER_API_KEY"
                : "OPENAI_API_KEY");
        }

        return new WfxSettings(
            provider,
            baseUri,
            string.IsNullOrWhiteSpace(apiKey) ? null : apiKey,
            merged.Model ?? string.Empty,
            merged.Headers ?? new Dictionary<string, string>(),
            TimeSpan.FromSeconds(Math.Clamp(merged.TimeoutSeconds ?? 300, 1, 3600)),
            Math.Clamp(merged.MaxIterations ?? 24, 1, 100),
            merged.Approval ?? ApprovalMode.Always);
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
        Approval = ParseApproval(GetEnvironment(environment, "WFX_APPROVAL"))
    };

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
            ? ParseApproval(value.GetString()) ?? throw new InvalidOperationException("Configuration approval must be always, workspace, or never.")
            : null;

    private static ApprovalMode? ParseApproval(string? value) => value?.ToLowerInvariant() switch
    {
        null or "" => null,
        "always" => ApprovalMode.Always,
        "workspace" => ApprovalMode.Workspace,
        "never" => ApprovalMode.Never,
        _ => null
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
