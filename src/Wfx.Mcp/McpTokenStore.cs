using System.Text.Json;

namespace Wfx.Mcp;

/// <summary>
/// One stored OAuth credential for a remote MCP server: the access token, the refresh token
/// and its expiry, plus the token endpoint and client identity needed to refresh without
/// re-running discovery. Tokens are secrets: they are never logged and never written to the
/// event stream.
/// </summary>
public sealed record McpTokenRecord(
    string ServerUrl,
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset? ExpiresAtUtc,
    string TokenEndpoint,
    string ClientId);

/// <summary>
/// The per-user MCP credential store at <c>%USERPROFILE%\.wfx\mcp-tokens.json</c>. Written
/// only by <c>wfx mcp auth</c>; read by the HTTP transport to attach bearer tokens. A missing
/// or corrupt file reads as empty rather than failing the CLI. Serialization is
/// <see cref="Utf8JsonWriter"/>/<see cref="JsonDocument"/> only, keeping Native AOT clean.
/// </summary>
public sealed class McpTokenStore
{
    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    public McpTokenStore(string path) => Path = path;

    /// <summary>The store under the given user profile root: <c>&lt;profile&gt;\.wfx\mcp-tokens.json</c>.</summary>
    public static McpTokenStore ForUserProfile(string? userProfile = null)
    {
        userProfile ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new McpTokenStore(System.IO.Path.Combine(userProfile, ".wfx", "mcp-tokens.json"));
    }

    public string Path { get; }

    public McpTokenRecord? Get(string serverName)
    {
        return Load().TryGetValue(serverName, out var record) ? record : null;
    }

    public void Save(string serverName, McpTokenRecord record)
    {
        var records = Load();
        records[serverName] = record;
        Write(records);
    }

    public bool Remove(string serverName)
    {
        var records = Load();
        if (!records.Remove(serverName))
        {
            return false;
        }

        Write(records);
        return true;
    }

    private Dictionary<string, McpTokenRecord> Load()
    {
        var records = new Dictionary<string, McpTokenRecord>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(Path))
        {
            return records;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(Path), ReadOptions);
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            // A corrupt or unreadable store reads as empty; the next 401 re-remediates sign-in.
            return records;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("servers", out var servers) ||
                servers.ValueKind != JsonValueKind.Object)
            {
                return records;
            }

            foreach (var property in servers.EnumerateObject())
            {
                if (ReadRecord(property.Value) is { } record)
                {
                    records[property.Name] = record;
                }
            }
        }

        return records;
    }

    private static McpTokenRecord? ReadRecord(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !TryGetString(element, "server_url", out var serverUrl) ||
            !TryGetString(element, "access_token", out var accessToken) ||
            !TryGetString(element, "token_endpoint", out var tokenEndpoint) ||
            !TryGetString(element, "client_id", out var clientId))
        {
            return null;
        }

        TryGetString(element, "refresh_token", out var refreshToken);
        DateTimeOffset? expiresAt = null;
        if (element.TryGetProperty("expires_at_utc", out var expiry) &&
            expiry.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(expiry.GetString(), out var parsed))
        {
            expiresAt = parsed;
        }

        return new McpTokenRecord(serverUrl!, accessToken!, refreshToken, expiresAt, tokenEndpoint!, clientId!);
    }

    private static bool TryGetString(JsonElement element, string property, out string? value)
    {
        value = null;
        if (element.TryGetProperty(property, out var child) && child.ValueKind == JsonValueKind.String)
        {
            value = child.GetString();
        }

        return !string.IsNullOrEmpty(value);
    }

    private void Write(Dictionary<string, McpTokenRecord> records)
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("servers");
            writer.WriteStartObject();
            foreach (var (name, record) in records.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                writer.WritePropertyName(name);
                writer.WriteStartObject();
                writer.WriteString("server_url", record.ServerUrl);
                writer.WriteString("access_token", record.AccessToken);
                if (record.RefreshToken is not null)
                {
                    writer.WriteString("refresh_token", record.RefreshToken);
                }

                if (record.ExpiresAtUtc is { } expiry)
                {
                    writer.WriteString("expires_at_utc", expiry.ToString("O"));
                }

                writer.WriteString("token_endpoint", record.TokenEndpoint);
                writer.WriteString("client_id", record.ClientId);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        // Write-then-replace so a crash mid-write cannot truncate an existing store.
        var temporary = Path + ".tmp";
        File.WriteAllBytes(temporary, buffer.ToArray());
        File.Move(temporary, Path, overwrite: true);
    }
}
