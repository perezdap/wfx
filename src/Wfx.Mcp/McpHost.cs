using Wfx.Core;

namespace Wfx.Mcp;

/// <summary>
/// Connects every user-configured MCP stdio server eagerly and assembles their tools into
/// the ordinary <see cref="ITool"/> surface. A server that fails to start, handshake, or
/// list tools degrades to a warning and contributes no tools; it never aborts the CLI.
/// Duplicate names after sanitizing are resolved first-wins with a warning.
/// </summary>
public sealed class McpHost : IAsyncDisposable
{
    private static readonly TimeSpan DefaultHandshakeTimeout = TimeSpan.FromSeconds(30);

    private readonly IReadOnlyList<McpStdioClient> _clients;

    private McpHost(IReadOnlyList<ITool> tools, IReadOnlyList<McpStdioClient> clients)
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
        TimeSpan? handshakeTimeout = null)
    {
        var timeout = handshakeTimeout ?? DefaultHandshakeTimeout;
        var connected = new List<(string ServerName, McpStdioClient Client, IReadOnlyList<McpToolInfo> Tools)>();
        foreach (var (serverName, server) in servers.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            McpStdioClient? client = null;
            IReadOnlyList<McpToolInfo> tools;
            try
            {
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                handshake.CancelAfter(timeout);
                client = await McpStdioClient.StartAsync(server, workspaceRoot, cancellationToken).ConfigureAwait(false);
                await client.InitializeAsync(handshake.Token).ConfigureAwait(false);
                tools = await client.ListToolsAsync(handshake.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (client is not null)
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                }

                throw;
            }
            catch (OperationCanceledException)
            {
                if (client is not null)
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                }

                warn($"MCP server '{serverName}' is unavailable: the handshake timed out after {timeout.TotalSeconds:F0}s.");
                continue;
            }
            catch (Exception exception)
            {
                if (client is not null)
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                }

                warn($"MCP server '{serverName}' is unavailable: {exception.Message}");
                continue;
            }

            connected.Add((serverName, client, tools));
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

    internal static McpHost Assemble(
        IReadOnlyList<(string ServerName, McpStdioClient Client, IReadOnlyList<McpToolInfo> Tools)> servers,
        Action<string> warn)
    {
        var tools = new List<ITool>();
        var clients = new List<McpStdioClient>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (serverName, client, infos) in servers)
        {
            clients.Add(client);
            foreach (var info in infos)
            {
                var name = McpToolNames.ForTool(serverName, info.Name);
                if (!names.Add(name))
                {
                    warn($"MCP tool '{info.Name}' from server '{serverName}' was skipped because the name '{name}' is already registered.");
                    continue;
                }

                tools.Add(new McpTool(serverName, info, client, name));
            }
        }

        return new McpHost(tools, clients);
    }
}
