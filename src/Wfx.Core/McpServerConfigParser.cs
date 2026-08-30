using System.Text.Json;

namespace Wfx.Core;

/// <summary>
/// Parses and validates the <c>mcp_servers</c> configuration map: each entry is exactly one
/// transport — <c>command</c> (stdio) or <c>url</c> (Streamable HTTP) — with the keys that
/// belong to it. Every rejection names the offending key and the file path. This is the
/// transport discrimination for <see cref="McpServerSettings"/>; the general loader in
/// <see cref="WfxConfiguration"/> calls in here.
/// </summary>
internal static class McpServerConfigParser
{
    public static IReadOnlyDictionary<string, McpServerSettings> Parse(JsonElement element, string path)
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
            var url = GetString(property.Value, "url");
            var hasCommand = !string.IsNullOrWhiteSpace(command);
            var hasUrl = !string.IsNullOrWhiteSpace(url);
            if (hasCommand == hasUrl)
            {
                throw new InvalidOperationException(
                    $"MCP server '{property.Name}' must define exactly one transport: " +
                    $"'command' for stdio or 'url' for HTTP (it defines {(hasCommand ? "both" : "neither")}): {path}");
            }

            if (hasUrl &&
                (!Uri.TryCreate(url, UriKind.Absolute, out var endpoint) ||
                 (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps)))
            {
                throw new InvalidOperationException(
                    $"MCP server '{property.Name}' 'url' must be an absolute http or https URL: {path}");
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

            Dictionary<string, string>? headers = null;
            if (property.Value.TryGetProperty("headers", out var headersElement))
            {
                if (headersElement.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException($"MCP server '{property.Name}' 'headers' must be an object with string values: {path}");
                }

                headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var header in headersElement.EnumerateObject())
                {
                    if (header.Value.ValueKind != JsonValueKind.String)
                    {
                        throw new InvalidOperationException($"MCP server '{property.Name}' header '{header.Name}' must be a string: {path}");
                    }

                    headers[header.Name] = header.Value.GetString()!;
                }
            }

            McpServerSettings server;
            if (hasUrl)
            {
                if (arguments.Count > 0 || property.Value.TryGetProperty("args", out _) ||
                    environment.Count > 0 || property.Value.TryGetProperty("env", out _))
                {
                    throw new InvalidOperationException(
                        $"MCP server '{property.Name}' is an HTTP server ('url') and cannot define 'args' or 'env': {path}");
                }

                server = McpServerSettings.ForHttp(url!, headers);
            }
            else
            {
                if (headers is not null)
                {
                    throw new InvalidOperationException(
                        $"MCP server '{property.Name}' is a stdio server ('command') and cannot define 'headers': {path}");
                }

                server = McpServerSettings.ForStdio(command!, arguments, environment);
            }

            if (!servers.TryAdd(property.Name, server))
            {
                throw new InvalidOperationException($"Configuration defines a duplicate MCP server '{property.Name}': {path}");
            }
        }

        return servers;
    }

    /// <summary>
    /// Raw-file check for the user-layer-only trust boundary: rejects a project configuration
    /// that declares <c>mcp_servers</c> at the top level or in any profile, even when the
    /// file is malformed enough to fail ordinary parsing.
    /// </summary>
    public static bool ProjectFileDeclaresMcpServers(string path)
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
        catch (JsonException)
        {
            // An unreadable file surfaces as the ordinary configuration parse error.
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (root.TryGetProperty("mcp_servers", out _))
            {
                return true;
            }

            if (root.TryGetProperty("profiles", out var profiles) && profiles.ValueKind == JsonValueKind.Object)
            {
                foreach (var profile in profiles.EnumerateObject())
                {
                    if (profile.Value.ValueKind == JsonValueKind.Object &&
                        profile.Value.TryGetProperty("mcp_servers", out _))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }

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
}
