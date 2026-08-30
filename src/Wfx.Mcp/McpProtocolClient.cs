using System.Text;
using System.Text.Json;

namespace Wfx.Mcp;

/// <summary>
/// The MCP handshake and tool surface shared by every transport: initialize,
/// notifications/initialized, tools/list with cursors, and tools/call with content/isError.
/// Only the byte-mover behind the <see cref="McpJsonRpcSession"/> differs between stdio and
/// Streamable HTTP.
/// </summary>
internal sealed class McpProtocolClient
{
    /// <summary>
    /// The protocol revision WFX offers in the handshake. Servers may negotiate down to any
    /// revision WFX supports; anything else refuses the connection.
    /// </summary>
    public const string OfferedProtocolVersion = "2025-06-18";

    /// <summary>
    /// Protocol revisions whose tool surface (initialize, tools/list with cursors,
    /// tools/call with content/isError) is compatible with this client.
    /// </summary>
    internal static readonly string[] SupportedProtocolVersions =
    [
        "2025-06-18",
        "2025-03-26",
        "2024-11-05"
    ];

    /// <summary>Caps tools/list pagination so a runaway cursor cannot loop forever.</summary>
    internal const int MaxToolPages = 16;

    private readonly McpJsonRpcSession _session;

    public McpProtocolClient(McpJsonRpcSession session) => _session = session;

    /// <summary>Runs the initialize handshake and returns the negotiated protocol version.</summary>
    public async Task<string> InitializeAsync(CancellationToken cancellationToken = default)
    {
        var result = await _session.RequestAsync("initialize", writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("protocolVersion", OfferedProtocolVersion);
            writer.WritePropertyName("capabilities");
            writer.WriteStartObject();
            writer.WriteEndObject();
            writer.WritePropertyName("clientInfo");
            writer.WriteStartObject();
            writer.WriteString("name", "wfx");
            writer.WriteString("version", "0.1.0");
            writer.WriteEndObject();
            writer.WriteEndObject();
        }, cancellationToken).ConfigureAwait(false);

        if (result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("protocolVersion", out var versionElement) ||
            versionElement.ValueKind != JsonValueKind.String)
        {
            throw new McpConnectionException("The MCP server returned an initialize result without a protocol version.");
        }

        var version = versionElement.GetString()!;
        if (!SupportedProtocolVersions.Contains(version))
        {
            throw new McpConnectionException(
                $"The MCP server negotiated protocol version '{version}', which WFX does not support.");
        }

        await _session.NotifyAsync("notifications/initialized", null, cancellationToken).ConfigureAwait(false);
        return version;
    }

    public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        var tools = new List<McpToolInfo>();
        string? cursor = null;
        for (var page = 0; page < MaxToolPages; page++)
        {
            var pageCursor = cursor;
            var result = await _session.RequestAsync("tools/list", writer =>
            {
                writer.WriteStartObject();
                if (pageCursor is not null)
                {
                    writer.WriteString("cursor", pageCursor);
                }

                writer.WriteEndObject();
            }, cancellationToken).ConfigureAwait(false);

            if (result.ValueKind != JsonValueKind.Object ||
                !result.TryGetProperty("tools", out var toolsElement) ||
                toolsElement.ValueKind != JsonValueKind.Array)
            {
                throw new McpConnectionException("The MCP server returned an invalid tools/list result.");
            }

            foreach (var item in toolsElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("name", out var nameElement) ||
                    nameElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var name = nameElement.GetString();
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                string? description = item.TryGetProperty("description", out var descriptionElement) &&
                    descriptionElement.ValueKind == JsonValueKind.String
                    ? descriptionElement.GetString()
                    : null;
                JsonElement? schema = item.TryGetProperty("inputSchema", out var schemaElement) &&
                    schemaElement.ValueKind == JsonValueKind.Object
                    ? schemaElement.Clone()
                    : null;
                tools.Add(new McpToolInfo(name, description, schema));
            }

            if (!result.TryGetProperty("nextCursor", out var nextCursorElement) ||
                nextCursorElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrEmpty(nextCursorElement.GetString()))
            {
                return tools;
            }

            cursor = nextCursorElement.GetString();
        }

        // The server kept paginating past the cap; surface what was listed rather than loop.
        return tools;
    }

    public async Task<McpToolCallResult> CallToolAsync(
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        var result = await _session.RequestAsync("tools/call", writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("name", toolName);
            if (arguments.ValueKind == JsonValueKind.Object)
            {
                writer.WritePropertyName("arguments");
                arguments.WriteTo(writer);
            }

            writer.WriteEndObject();
        }, cancellationToken).ConfigureAwait(false);

        if (result.ValueKind != JsonValueKind.Object)
        {
            throw new McpConnectionException("The MCP server returned an invalid tools/call result.");
        }

        var isError = result.TryGetProperty("isError", out var isErrorElement) &&
            isErrorElement.ValueKind == JsonValueKind.True;

        var output = new StringBuilder();
        if (result.TryGetProperty("content", out var contentElement) && contentElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in contentElement.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object ||
                    !part.TryGetProperty("type", out var typeElement) ||
                    typeElement.ValueKind != JsonValueKind.String ||
                    !string.Equals(typeElement.GetString(), "text", StringComparison.Ordinal) ||
                    !part.TryGetProperty("text", out var textElement) ||
                    textElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                if (output.Length > 0)
                {
                    output.AppendLine();
                }

                output.Append(textElement.GetString());
            }
        }
        else if (result.TryGetProperty("structuredContent", out var structuredElement))
        {
            output.Append(structuredElement.GetRawText());
        }

        return new McpToolCallResult(isError, output.ToString());
    }
}
