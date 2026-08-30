using System.Text.Json;
using Wfx.Core;

namespace Wfx.Mcp;

/// <summary>
/// One MCP Streamable HTTP server connection: JSON-RPC messages POSTed to a remote endpoint,
/// with responses arriving as JSON bodies or SSE event payloads. The handshake and tool
/// surface come from <see cref="McpProtocolClient"/>; only the byte-mover differs from stdio.
/// Failures surface as <see cref="McpConnectionException"/> (or
/// <see cref="McpAuthorizationException"/> when the endpoint demands sign-in); they never abort
/// the CLI or the turn.
/// </summary>
internal sealed class McpHttpClient : IMcpServerConnection
{
    private readonly McpJsonRpcSession _session;
    private readonly McpProtocolClient _protocol;
    private readonly McpHttpTransport _transport;
    private bool _disposed;

    private McpHttpClient(McpJsonRpcSession session, McpHttpTransport transport)
    {
        _session = session;
        _protocol = new McpProtocolClient(session);
        _transport = transport;
    }

    public static McpHttpClient Start(
        McpServerSettings server,
        string serverName,
        HttpClient? httpClient = null,
        McpTokenStore? tokenStore = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (server.Url is not { Length: > 0 } url || !Uri.TryCreate(url, UriKind.Absolute, out var endpoint))
        {
            throw new McpConnectionException($"MCP server '{serverName}' does not define an absolute HTTP url.");
        }

        var credential = tokenStore is null ? null : new McpHttpCredential(tokenStore, serverName);
        var transport = new McpHttpTransport(endpoint, serverName, server.Headers, httpClient, credential);
        var session = new McpJsonRpcSession(transport.Output, transport.Input);
        session.StartReadLoop();
        return new McpHttpClient(session, transport);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var negotiated = await _protocol.InitializeAsync(cancellationToken).ConfigureAwait(false);
        _transport.SetProtocolVersion(negotiated);
    }

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
        await _transport.DisposeAsync().ConfigureAwait(false);
    }
}
