using Wfx.Core;

namespace Wfx.Mcp;

/// <summary>
/// Connects every user-configured MCP server eagerly — stdio and Streamable HTTP alike — and
/// assembles their tools into the ordinary <see cref="ITool"/> surface. A server that fails
/// to start, handshake, or list tools degrades to a warning and contributes no tools; a
/// server that demands OAuth sign-in is reported through <see cref="AuthorizationReminders"/>
/// instead. Duplicate names after sanitizing are resolved first-wins with a warning. The host
/// also owns the single <see cref="HttpClient"/> all MCP traffic (connect, sign-in, token
/// refresh) shares, the per-user credential store behind <c>wfx mcp auth</c>, and the live
/// redaction set behind <see cref="Secrets"/>. This is the only public surface of the
/// assembly; the client, adapter, transport, and OAuth types are implementation detail.
/// </summary>
public sealed class McpHost : IAsyncDisposable
{
    private static readonly TimeSpan DefaultHandshakeTimeout = TimeSpan.FromSeconds(30);

    private readonly IReadOnlyList<IMcpServerConnection> _clients;
    private readonly McpTokenStore? _store;
    private readonly McpSecretSet _secrets;
    private readonly HttpClient? _http;

    private McpHost(
        IReadOnlyList<ITool> tools,
        IReadOnlyList<IMcpServerConnection> clients,
        IReadOnlyList<McpAuthorizationReminder> reminders,
        McpTokenStore? store,
        McpSecretSet secrets,
        HttpClient? httpClient)
    {
        Tools = tools;
        AuthorizationReminders = reminders;
        _clients = clients;
        _store = store;
        _secrets = secrets;
        _http = httpClient;
    }

    public IReadOnlyList<ITool> Tools { get; }

    /// <summary>
    /// The secrets MCP handling must never leak to logs, approval prompts, or the event
    /// stream: configured header values plus every stored OAuth token. The set is live — a
    /// token minted by a mid-run refresh joins it immediately.
    /// </summary>
    public IReadOnlyList<string> Secrets => _secrets;

    /// <summary>
    /// The servers whose handshake was refused with a 401 and that hold no usable credential.
    /// Each carries the sign-in remediation; surfacing it is the caller's contract with the
    /// user, so it is never treated as suppressible chatter.
    /// </summary>
    public IReadOnlyList<McpAuthorizationReminder> AuthorizationReminders { get; }

    public static Task<McpHost> ConnectAsync(
        IReadOnlyDictionary<string, McpServerSettings> servers,
        string workspaceRoot,
        Action<string> warn,
        CancellationToken cancellationToken = default,
        TimeSpan? handshakeTimeout = null,
        string? userProfile = null) =>
        ConnectAsync(servers, workspaceRoot, warn, McpTokenStore.ForUserProfile(userProfile), cancellationToken, handshakeTimeout);

    internal static async Task<McpHost> ConnectAsync(
        IReadOnlyDictionary<string, McpServerSettings> servers,
        string workspaceRoot,
        Action<string> warn,
        McpTokenStore tokenStore,
        CancellationToken cancellationToken = default,
        TimeSpan? handshakeTimeout = null)
    {
        var timeout = handshakeTimeout ?? DefaultHandshakeTimeout;
        var secrets = new McpSecretSet();
        foreach (var server in servers.Values)
        {
            foreach (var value in server.Headers.Values)
            {
                secrets.Add(value);
            }
        }

        foreach (var record in tokenStore.LoadAll().Values)
        {
            secrets.Add(record.AccessToken);
            secrets.Add(record.RefreshToken);
        }

        // One MCP-owned client carries every server's traffic; the CLI's client is the
        // model-provider seam and never touches MCP.
        var http = servers.Values.Any(static server => server.IsHttp)
            ? new HttpClient { Timeout = Timeout.InfiniteTimeSpan }
            : null;
        var connected = new List<ConnectedServer>();
        var reminders = new List<McpAuthorizationReminder>();
        try
        {
            foreach (var (serverName, server) in servers.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ConnectedServer? result = null;
                try
                {
                    result = await ConnectServerAsync(
                            serverName, server, workspaceRoot, timeout, http, tokenStore, secrets, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (McpAuthorizationException exception)
                {
                    await DisposeQuietly(result).ConfigureAwait(false);
                    reminders.Add(new McpAuthorizationReminder(
                        serverName, McpSignInRemediation.Command(serverName), exception.Message));
                    continue;
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
        }
        catch
        {
            http?.Dispose();
            throw;
        }

        return Assemble(connected, warn, reminders, tokenStore, secrets, http);
    }

    /// <summary>
    /// Creates a host for the explicit <c>wfx mcp auth</c> sign-in and revocation commands:
    /// no servers are connected, but the credential store and HTTP client are the same ones a
    /// connected host uses.
    /// </summary>
    public static McpHost CreateAuthorizer(string? userProfile = null) =>
        new([], [], [], McpTokenStore.ForUserProfile(userProfile), new McpSecretSet(), new HttpClient { Timeout = Timeout.InfiniteTimeSpan });

    /// <summary>
    /// Runs the interactive OAuth 2.1 (authorization code + PKCE) sign-in for one configured
    /// HTTP server: metadata discovery, dynamic client registration when advertised, loopback
    /// browser redirect, token exchange, and persistence to the per-user store. Never runs
    /// mid-turn; the outcome is returned for the caller to present.
    /// </summary>
    public Task<McpAuthorizationResult> AuthorizeAsync(
        string serverName,
        IReadOnlyDictionary<string, McpServerSettings> servers,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default) =>
        AuthorizeAsync(serverName, servers, redirect: null, progress, cancellationToken);

    internal async Task<McpAuthorizationResult> AuthorizeAsync(
        string serverName,
        IReadOnlyDictionary<string, McpServerSettings> servers,
        IMcpAuthorizationRedirect? redirect,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!servers.TryGetValue(serverName, out var server))
        {
            return new McpAuthorizationResult(
                McpAuthorizationOutcome.ServerNotConfigured,
                $"MCP server '{serverName}' is not configured in the user configuration.");
        }

        if (!server.IsHttp)
        {
            return new McpAuthorizationResult(
                McpAuthorizationOutcome.ServerNotHttp,
                $"MCP server '{serverName}' is a stdio server; sign-in applies to HTTP servers only.");
        }

        using var owned = redirect is null ? new McpLoopbackBrowserRedirect() : null;
        try
        {
            await new McpOAuthFlow(Http, _store!).AuthorizeAsync(
                    serverName, server, redirect ?? owned!, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            return new McpAuthorizationResult(
                McpAuthorizationOutcome.Failed,
                $"sign-in to MCP server '{serverName}' failed: {exception.Message}");
        }

        return new McpAuthorizationResult(
            McpAuthorizationOutcome.SignedIn,
            $"signed in to MCP server '{serverName}'. The credential is stored in {_store!.Path}.");
    }

    /// <summary>
    /// Drops the stored credential for a server. Works even for a server that is no longer
    /// configured, so stale credentials are never stranded.
    /// </summary>
    public McpAuthorizationResult Revoke(string serverName) =>
        _store is not null && _store.Remove(serverName)
            ? new McpAuthorizationResult(
                McpAuthorizationOutcome.CredentialRemoved,
                $"removed the stored credential for MCP server '{serverName}'.")
            : new McpAuthorizationResult(
                McpAuthorizationOutcome.NoStoredCredential,
                $"no stored credential for MCP server '{serverName}'.");

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }

        _http?.Dispose();
    }

    internal static McpHost Assemble(IReadOnlyList<ConnectedServer> servers, Action<string> warn) =>
        Assemble(servers, warn, [], store: null, new McpSecretSet(), http: null);

    private static McpHost Assemble(
        IReadOnlyList<ConnectedServer> servers,
        Action<string> warn,
        IReadOnlyList<McpAuthorizationReminder> reminders,
        McpTokenStore? store,
        McpSecretSet secrets,
        HttpClient? http)
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

        return new McpHost(tools, clients, reminders, store, secrets, http);
    }

    private static async Task<ConnectedServer> ConnectServerAsync(
        string serverName,
        McpServerSettings server,
        string workspaceRoot,
        TimeSpan timeout,
        HttpClient? http,
        McpTokenStore tokenStore,
        McpSecretSet secrets,
        CancellationToken cancellationToken)
    {
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        handshake.CancelAfter(timeout);
        var client = server.IsHttp
            ? (IMcpServerConnection)McpHttpTransport.Start(server, serverName, http!, tokenStore, secrets, cancellationToken)
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

    private HttpClient Http => _http ?? throw new InvalidOperationException("This MCP host owns no HTTP client.");

    internal sealed record ConnectedServer(string Name, IMcpServerConnection Client, IReadOnlyList<McpToolInfo> Tools);
}
