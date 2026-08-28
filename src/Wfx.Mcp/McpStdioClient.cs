using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wfx.Core;
using Wfx.PowerShell;

namespace Wfx.Mcp;

/// <summary>
/// One long-lived MCP stdio server connection: a child process speaking newline-delimited
/// JSON-RPC 2.0 over stdin/stdout. Start failures, crashes, and invalid responses surface
/// as <see cref="McpConnectionException"/>; they never abort the CLI or the turn. Cancelling
/// an in-flight call disposes the client, which kills the server's process tree.
/// </summary>
public sealed class McpStdioClient : IAsyncDisposable
{
    /// <summary>
    /// The protocol revision WFX offers in the handshake. The server's negotiated revision in
    /// its response is accepted as-is; the milestone supports the shared stdio tool surface.
    /// </summary>
    public const string OfferedProtocolVersion = "2025-06-18";

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

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
    internal ProcessOwner? Owner { get; private set; }

    public static async Task<McpStdioClient> StartAsync(
        McpServerSettings server,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = server.Command,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom
        };
        foreach (var argument in server.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // Secret-scrubbed environment defaults apply to MCP servers like every other child
        // process; configured env values overlay them and may reintroduce variables by name.
        var overlay = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in server.Environment)
        {
            overlay[pair.Key] = pair.Value;
        }

        ChildProcessEnvironment.Apply(startInfo.Environment, overlay);

        var process = new Process { StartInfo = startInfo };
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!process.Start())
            {
                throw new InvalidOperationException($"The operating system refused to start '{server.Command}'.");
            }
        }
        catch (OperationCanceledException)
        {
            process.Dispose();
            throw;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            throw new McpConnectionException(
                $"Could not start MCP server command '{server.Command}': {exception.Message}",
                exception);
        }

        var owner = new ProcessOwner(process);
        var session = new McpJsonRpcSession(process.StandardInput, process.StandardOutput);
        var drain = Task.Run(() => DrainAsync(process.StandardError));
        session.StartReadLoop();
        return new McpStdioClient(session, owner, drain) { Owner = owner };
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var parameters = new JsonObject
        {
            ["protocolVersion"] = OfferedProtocolVersion,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject
            {
                ["name"] = "wfx",
                ["version"] = "0.1.0"
            }
        };
        var result = await _session.RequestAsync("initialize", parameters, cancellationToken).ConfigureAwait(false);
        if (result.ValueKind != JsonValueKind.Object)
        {
            throw new McpConnectionException("The MCP server returned an invalid initialize result.");
        }

        // The negotiated protocolVersion rides on the result; the milestone uses the shared
        // tool surface, so the server's revision is accepted without gating.
        await _session.NotifyAsync("notifications/initialized", null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        var result = await _session.RequestAsync("tools/list", new JsonObject(), cancellationToken).ConfigureAwait(false);
        if (result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("tools", out var toolsElement) ||
            toolsElement.ValueKind != JsonValueKind.Array)
        {
            throw new McpConnectionException("The MCP server returned an invalid tools/list result.");
        }

        var tools = new List<McpToolInfo>();
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
            JsonNode? schema = item.TryGetProperty("inputSchema", out var schemaElement) &&
                schemaElement.ValueKind == JsonValueKind.Object
                ? JsonNode.Parse(schemaElement.GetRawText())
                : null;
            tools.Add(new McpToolInfo(name, description, schema));
        }

        return tools;
    }

    public async Task<McpToolCallResult> CallToolAsync(
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        var parameters = new JsonObject
        {
            ["name"] = toolName
        };
        if (arguments.ValueKind == JsonValueKind.Object)
        {
            parameters["arguments"] = JsonNode.Parse(arguments.GetRawText());
        }

        var result = await _session.RequestAsync("tools/call", parameters, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Owns the server process lifetime: disposal kills the entire process tree and waits
    /// for exit, mirroring <see cref="ProcessExecutor"/>'s cancellation discipline.
    /// </summary>
    internal sealed class ProcessOwner(Process process) : IAsyncDisposable
    {
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Process Process => process;

        /// <summary>Completes once the process has exited and disposal has observed it.</summary>
        internal Task Exited => _exited.Task;

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // The process exited between the check and Kill.
            }

            try
            {
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // The process is already gone.
            }

            process.Dispose();
            _exited.TrySetResult();
        }
    }
}
