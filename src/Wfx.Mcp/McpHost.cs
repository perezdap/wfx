using Wfx.Core;

namespace Wfx.Mcp;

/// <summary>
/// Connects every user-configured MCP stdio server eagerly and assembles their tools into
/// the ordinary <see cref="ITool"/> surface. A server that fails to start, handshake, or
/// list tools degrades to a warning and contributes no tools; it never aborts the CLI.
/// Duplicate names after sanitizing are resolved first-wins with a warning. This is the
/// only public surface of the assembly; the client, adapter, and protocol types are
/// implementation detail.
/// </summary>
public sealed class McpHost : IAsyncDisposable
{
    private static readonly TimeSpan DefaultHandshakeTimeout = TimeSpan.FromSeconds(30);

    private readonly IReadOnlyList<IMcpServerConnection> _clients;

    private McpHost(IReadOnlyList<ITool> tools, IReadOnlyList<IMcpServerConnection> clients)
    {
        Tools = tools;
        _clients = clients;
    }

    public IReadOnlyList<ITool> Tools { get; }

    public static async Task<McpHost> ConnectAsync(
        IReadOnlyDictionary<string, McpServerSettings> servers,
        string workspaceRoot,
        Action<string> warn,
        CancellationToken cancellationToken = default,
        TimeSpan? handshakeTimeout = null,
        HttpClient? httpClient = null,
        McpTokenStore? tokenStore = null)
    {
        var timeout = handshakeTimeout ?? DefaultHandshakeTimeout;
        var connected = new List<ConnectedServer>();
        foreach (var (serverName, server) in servers.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConnectedServer? result = null;
            try
            {
                result = await ConnectServerAsync(serverName, server, workspaceRoot, timeout, httpClient, tokenStore, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await DisposeQuietly(result).ConfigureAwait(false);
                throw;
            }
            catch (OperationCanceledException)
            {
                await DisposeQuietly(result).ConfigureAwait(false);
                warn($"MCP server '{serverName}' is unavailable: the handshake timed out after {timeout.TotalSeconds:F0}s.");
                continue;
            }
            catch (Exception exception)
            {
                await DisposeQuietly(result).ConfigureAwait(false);
                warn($"MCP server '{serverName}' is unavailable: {exception.Message}");
                continue;
            }

            connected.Add(result);
        }

        return Assemble(connected, warn);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal static McpHost Assemble(IReadOnlyList<ConnectedServer> servers, Action<string> warn)
    {
        var tools = new List<ITool>();
        var clients = new List<IMcpServerConnection>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var server in servers)
        {
            clients.Add(server.Client);
            foreach (var info in server.Tools)
            {
                var name = McpToolNames.ForTool(server.Name, info.Name);
                if (!names.Add(name))
                {
                    warn($"MCP tool '{info.Name}' from server '{server.Name}' was skipped because the name '{name}' is already registered.");
                    continue;
                }

                tools.Add(new McpTool(server.Name, info, server.Client, name));
            }
        }

        return new McpHost(tools, clients);
    }

    private static async Task<ConnectedServer> ConnectServerAsync(
        string serverName,
        McpServerSettings server,
        string workspaceRoot,
        TimeSpan timeout,
        HttpClient? httpClient,
        McpTokenStore? tokenStore,
        CancellationToken cancellationToken)
    {
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        handshake.CancelAfter(timeout);
        var client = server.IsHttp
            ? (IMcpServerConnection)McpHttpClient.Start(server, serverName, httpClient, tokenStore, cancellationToken)
            : McpStdioClient.Start(server, workspaceRoot, cancellationToken);
        try
        {
            await client.InitializeAsync(handshake.Token).ConfigureAwait(false);
            var tools = await client.ListToolsAsync(handshake.Token).ConfigureAwait(false);
            return new ConnectedServer(serverName, client, tools);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static ValueTask DisposeQuietly(ConnectedServer? server) =>
        server is null ? ValueTask.CompletedTask : server.Client.DisposeAsync();

    internal sealed record ConnectedServer(string Name, IMcpServerConnection Client, IReadOnlyList<McpToolInfo> Tools);
}
