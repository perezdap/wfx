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
internal sealed class McpStdioClient : IMcpServerConnection
{
    /// <inheritdoc cref="McpProtocolClient.OfferedProtocolVersion"/>
    public const string OfferedProtocolVersion = McpProtocolClient.OfferedProtocolVersion;

    private readonly McpJsonRpcSession _session;
    private readonly McpProtocolClient _protocol;
    private readonly IAsyncDisposable _process;
    private readonly Task _stderrDrain;
    private bool _disposed;

    internal McpStdioClient(McpJsonRpcSession session, IAsyncDisposable process, Task? stderrDrain = null)
    {
        _session = session;
        _protocol = new McpProtocolClient(session);
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
        if (server.Command is not { Length: > 0 } command)
        {
            throw new McpConnectionException("The MCP server entry does not define a stdio command.");
        }

        ChildProcessSession session;
        try
        {
            session = new ProcessExecutor().StartSession(new ProcessCommand(
                command,
                server.Arguments,
                workspaceRoot,
                Environment: ToOverlay(server.Environment)));
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
            or InvalidOperationException or DirectoryNotFoundException)
        {
            throw new McpConnectionException(
                $"Could not start MCP server command '{command}': {exception.Message}",
                exception);
        }

        var rpc = new McpJsonRpcSession(session.StandardInput, session.StandardOutput);
        var drain = Task.Run(() => DrainAsync(session.StandardError));
        rpc.StartReadLoop();
        return new McpStdioClient(rpc, session, drain) { Owner = session };
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default) =>
        await _protocol.InitializeAsync(cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default) =>
        await _protocol.ListToolsAsync(cancellationToken).ConfigureAwait(false);

    public async Task<McpToolCallResult> CallToolAsync(
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken = default) =>
        await _protocol.CallToolAsync(toolName, arguments, cancellationToken).ConfigureAwait(false);

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
