using System.Text;
using System.Text.Json;
using Wfx.Core;
using Wfx.PowerShell;

namespace Wfx.Mcp;

/// <summary>
/// One long-lived MCP stdio server connection: a child process speaking newline-delimited
/// JSON-RPC 2.0 over stdin/stdout. Process launching, environment scrubbing, and kill
/// discipline come from <see cref="ProcessExecutor"/>/<see cref="ChildProcessSession"/>.
/// Start failures, crashes, and invalid responses surface as <see cref="McpConnectionException"/>;
/// they never abort the CLI or the turn. Cancelling an in-flight call disposes the client,
/// which kills the server's process tree.
/// </summary>
internal sealed class McpStdioClient : IAsyncDisposable
{
    /// <summary>
    /// The protocol revision WFX offers in the handshake. Servers may negotiate down to any
    /// revision WFX supports; anything else refuses the connection.
    /// </summary>
    public const string OfferedProtocolVersion = "2025-06-18";

    /// <summary>
    /// Protocol revisions whose stdio tool surface (initialize, tools/list with cursors,
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
    private readonly IAsyncDisposable _process;
    private readonly Task _stderrDrain;
    private bool _disposed;

    internal McpStdioClient(McpJsonRpcSession session, IAsyncDisposable process, Task? stderrDrain = null)
    {
        _session = session;
        _process = process;
        _stderrDrain = stderrDrain ?? Task.CompletedTask;
    }

    /// <summary>Exposed for tests that verify cancellation kills the real server process.</summary>
    internal ChildProcessSession? Owner { get; private set; }

    public static McpStdioClient Start(
        McpServerSettings server,
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ChildProcessSession session;
        try
        {
            session = new ProcessExecutor().StartSession(new ProcessCommand(
                server.Command,
                server.Arguments,
                workspaceRoot,
                Environment: ToOverlay(server.Environment)));
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
            or InvalidOperationException or DirectoryNotFoundException)
        {
            throw new McpConnectionException(
                $"Could not start MCP server command '{server.Command}': {exception.Message}",
                exception);
        }

        var rpc = new McpJsonRpcSession(session.StandardInput, session.StandardOutput);
        var drain = Task.Run(() => DrainAsync(session.StandardError));
        rpc.StartReadLoop();
        return new McpStdioClient(rpc, session, drain) { Owner = session };
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
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

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _session.Fault("The MCP connection was closed.");
        await _process.DisposeAsync().ConfigureAwait(false);
        try
        {
            await _stderrDrain.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The stderr drain observes a killed process; its failure adds nothing.
        }
    }

    private static IReadOnlyDictionary<string, string?>? ToOverlay(IReadOnlyDictionary<string, string> environment)
    {
        if (environment.Count == 0)
        {
            return null;
        }

        var overlay = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in environment)
        {
            overlay[pair.Key] = pair.Value;
        }

        return overlay;
    }

    private static async Task DrainAsync(StreamReader stderr)
    {
        var buffer = new char[4 * 1024];
        try
        {
            // Server stderr is drained so a chatty server cannot block on a full pipe; the
            // content itself is discarded.
            while (await stderr.ReadAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false) > 0)
            {
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Pipe closed with the process; nothing to report.
        }
    }
}
