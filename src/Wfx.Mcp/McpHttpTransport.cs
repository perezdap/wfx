using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Wfx.Core;

namespace Wfx.Mcp;

/// <summary>
/// One MCP Streamable HTTP server connection: the byte-mover and the session lifetime in one
/// type. Shaped as the TextWriter/TextReader pair <see cref="McpJsonRpcSession"/> already
/// speaks: every written line is one HTTP POST of a JSON-RPC message, and every response
/// payload (a JSON body or the data frames of an SSE stream) is fed back as incoming lines.
/// Per the spec, a notification POST is answered 202 with no body; a request POST is answered
/// with either a single JSON body or an SSE stream that carries the matching response and
/// then ends. A 401 surfaces as <see cref="McpAuthorizationException"/> with the sign-in
/// remediation; other non-2xx statuses and network failures surface as
/// <see cref="McpConnectionException"/> and never abort the CLI. The
/// <see cref="HttpClient"/> is always supplied by the caller (the host owns one for all MCP
/// traffic); this type never creates or disposes one.
/// </summary>
internal sealed class McpHttpTransport : IMcpServerConnection
{
    /// <summary>Caps one incoming message, mirroring <see cref="McpJsonRpcSession.MaxLineCharacters"/>.</summary>
    private const int MaxMessageCharacters = McpJsonRpcSession.MaxLineCharacters;

    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _serverName;
    private readonly IReadOnlyDictionary<string, string> _headers;
    private readonly Channel<string> _incoming = Channel.CreateUnbounded<string>();
    private readonly McpHttpCredential? _credential;
    private readonly McpJsonRpcSession _session;
    private readonly McpProtocolClient _protocol;
    private volatile string? _sessionId;
    private volatile string? _protocolVersion;
    private bool _disposed;

    private McpHttpTransport(
        Uri endpoint,
        string serverName,
        IReadOnlyDictionary<string, string> headers,
        HttpClient httpClient,
        McpHttpCredential? credential)
    {
        _endpoint = endpoint;
        _serverName = serverName;
        _headers = headers;
        _http = httpClient;
        _credential = credential;
        Output = new HttpPostTextWriter(this);
        Input = new ChannelTextReader(_incoming.Reader);
        _session = new McpJsonRpcSession(Output, Input);
        _protocol = new McpProtocolClient(_session);
        _session.StartReadLoop();
    }

    /// <summary>The session's outgoing side: one POST per line.</summary>
    public TextWriter Output { get; }

    /// <summary>The session's incoming side: response payloads, one message per line.</summary>
    public TextReader Input { get; }

    public static McpHttpTransport Start(
        McpServerSettings server,
        string serverName,
        HttpClient httpClient,
        McpTokenStore? tokenStore = null,
        McpSecretSet? secrets = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (server.Url is not { Length: > 0 } url || !Uri.TryCreate(url, UriKind.Absolute, out var endpoint))
        {
            throw new McpConnectionException($"MCP server '{serverName}' does not define an absolute HTTP url.");
        }

        var credential = tokenStore is null ? null : new McpHttpCredential(tokenStore, serverName, secrets);
        return new McpHttpTransport(endpoint, serverName, server.Headers, httpClient, credential);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var negotiated = await _protocol.InitializeAsync(cancellationToken).ConfigureAwait(false);
        // Advertised on every request after the handshake, per the protocol header rule.
        _protocolVersion = negotiated;
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
        _incoming.Writer.TryComplete();
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    private async Task PostAsync(string line, CancellationToken cancellationToken)
    {
        // A notification has no id and gets no response: a streamed answer would have nothing
        // to match, so the stream is not read at all.
        var requestId = TryReadRequestId(line);
        await PostOnceAsync(line, requestId, allowAuthRetry: true, cancellationToken).ConfigureAwait(false);
    }

    private async Task PostOnceAsync(
        string line,
        long? requestId,
        bool allowAuthRetry,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(line, Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        foreach (var (name, value) in _headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        if (_sessionId is { } sessionId)
        {
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
        }

        if (_protocolVersion is { } protocolVersion)
        {
            request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", protocolVersion);
        }

        // A stored OAuth credential takes precedence over a configured Authorization header:
        // assigning the property replaces any value TryAddWithoutValidation attached above.
        string? accessToken = null;
        if (_credential is not null)
        {
            accessToken = await _credential.AcquireAccessTokenAsync(_http, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(accessToken))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            }
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new McpConnectionException(
                $"Could not reach MCP server '{_serverName}': {exception.Message}", exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new McpConnectionException($"MCP server '{_serverName}' timed out.", exception);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized)
            {
                if (allowAuthRetry &&
                    _credential is not null &&
                    await _credential.RefreshAccessTokenAsync(_http, accessToken, cancellationToken).ConfigureAwait(false))
                {
                    await PostOnceAsync(line, requestId, allowAuthRetry: false, cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                throw new McpAuthorizationException(McpSignInRemediation.Message(_serverName));
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new McpConnectionException(
                    $"MCP server '{_serverName}' returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
            }

            if (response.Headers.TryGetValues("Mcp-Session-Id", out var values))
            {
                foreach (var value in values)
                {
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        _sessionId = value;
                        break;
                    }
                }
            }

            if (response.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.NoContent)
            {
                return;
            }

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (string.Equals(contentType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                await ReadEventStreamAsync(response, requestId, cancellationToken).ConfigureAwait(false);
                return;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(body))
            {
                Feed(body);
            }
        }
    }

    /// <summary>
    /// Reads a POST response SSE stream, feeding every event payload, until the response for
    /// the outgoing request arrives or the server hangs up — the spec lets the server close
    /// the stream once the response is sent, but does not require it.
    /// </summary>
    private async Task ReadEventStreamAsync(
        HttpResponseMessage response,
        long? requestId,
        CancellationToken cancellationToken)
    {
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        var data = new StringBuilder();
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    var payload = data.ToString();
                    data.Clear();
                    Feed(payload);
                    if (requestId is { } expected && PayloadMatchesId(payload, expected))
                    {
                        return;
                    }
                }

                continue;
            }

            if (line[0] == ':')
            {
                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var value = line["data:".Length..];
                if (value.StartsWith(' '))
                {
                    value = value[1..];
                }

                if (data.Length > 0)
                {
                    data.Append('\n');
                }

                data.Append(value);
            }
        }
    }

    private void Feed(string payload)
    {
        if (payload.Length > MaxMessageCharacters)
        {
            payload = payload[..MaxMessageCharacters];
        }

        _incoming.Writer.TryWrite(payload);
    }

    private static long? TryReadRequestId(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("id", out var id) &&
                id.ValueKind == JsonValueKind.Number &&
                id.TryGetInt64(out var value))
            {
                return value;
            }
        }
        catch (JsonException)
        {
            // The session framed this line; treat an unparseable id as a notification.
        }

        return null;
    }

    private static bool PayloadMatchesId(string payload, long expected)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("id", out var id) &&
                ((id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var value) && value == expected) ||
                 (id.ValueKind == JsonValueKind.String && long.TryParse(id.GetString(), out value) && value == expected));
        }
        catch (JsonException)
        {
            // Malformed payloads still reach the session, which faults them structurally.
            return false;
        }
    }

    private sealed class HttpPostTextWriter(McpHttpTransport transport) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default) =>
            transport.PostAsync(buffer.ToString(), cancellationToken);

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ChannelTextReader(ChannelReader<string> reader) : TextReader
    {
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }
    }
}
