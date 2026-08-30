using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Wfx.Core;

namespace Wfx.Mcp;

/// <summary>
/// How the authorization step of an MCP OAuth flow reaches the user: the interactive
/// implementation opens the system browser and listens on a loopback redirect; tests substitute
/// a headless HTTP client. The redirect URI is known before the authorize URL is built.
/// </summary>
public interface IMcpAuthorizationRedirect
{
    /// <summary>The loopback redirect URI registered in the authorize request.</summary>
    Uri RedirectUri { get; }

    /// <summary>Presents the authorization URL and returns the callback URI carrying code and state.</summary>
    Task<Uri> WaitForCallbackAsync(Uri authorizationUrl, CancellationToken cancellationToken);
}

/// <summary>
/// OAuth 2.1 authorization-code-with-PKCE acquisition for a remote MCP server, per the MCP
/// authorization spec: protected-resource metadata discovery, authorization-server metadata,
/// dynamic client registration when the server advertises it (otherwise a fixed public client
/// id), and a loopback redirect. Runs only as the explicit <c>wfx mcp auth</c> command — never
/// mid-turn. The resulting credential lands in the per-user <see cref="McpTokenStore"/>.
/// </summary>
public sealed class McpOAuthFlow(HttpClient httpClient, McpTokenStore store)
{
    private const string FallbackClientId = "wfx";

    private readonly HttpClient _httpClient = httpClient;
    private readonly McpTokenStore _store = store;

    public async Task AuthorizeAsync(
        string serverName,
        McpServerSettings server,
        IMcpAuthorizationRedirect redirect,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (server.Url is not { Length: > 0 } url)
        {
            throw new InvalidOperationException($"MCP server '{serverName}' is not an HTTP server and needs no OAuth sign-in.");
        }

        var serverUri = new Uri(url, UriKind.Absolute);
        progress?.Invoke($"Discovering the authorization server for '{serverName}'...");
        var metadata = await DiscoverAuthorizationServerAsync(serverUri, cancellationToken).ConfigureAwait(false);

        var clientId = metadata.RegistrationEndpoint is { } registration
            ? await RegisterClientAsync(registration, redirect.RedirectUri, cancellationToken).ConfigureAwait(false)
            : FallbackClientId;

        var verifier = NewUrlSafeToken(32);
        var challenge = ComputeS256(verifier);
        var state = NewUrlSafeToken(16);
        var authorizationUrl = BuildAuthorizationUrl(metadata, clientId, redirect.RedirectUri, challenge, state, url);

        progress?.Invoke($"Signing in to MCP server '{serverName}'. Complete the sign-in in the browser window.");
        progress?.Invoke($"If the browser does not open, visit: {authorizationUrl}");
        var callback = await redirect.WaitForCallbackAsync(authorizationUrl, cancellationToken).ConfigureAwait(false);
        var parameters = ParseQuery(callback.Query);
        if (parameters.TryGetValue("error", out var error))
        {
            throw new InvalidOperationException(
                $"Authorization for MCP server '{serverName}' failed: {error}.");
        }

        if (!parameters.TryGetValue("state", out var returnedState) ||
            !string.Equals(returnedState, state, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Authorization for MCP server '{serverName}' returned a mismatched state; refusing the exchange.");
        }

        if (!parameters.TryGetValue("code", out var code) || string.IsNullOrEmpty(code))
        {
            throw new InvalidOperationException(
                $"Authorization for MCP server '{serverName}' returned no authorization code.");
        }

        var tokens = await ExchangeCodeAsync(metadata.TokenEndpoint, code, verifier, redirect.RedirectUri, clientId, url, cancellationToken)
            .ConfigureAwait(false);
        _store.Save(serverName, new McpTokenRecord(
            url,
            tokens.AccessToken,
            tokens.RefreshToken,
            tokens.ExpiresAtUtc,
            metadata.TokenEndpoint.ToString(),
            clientId));
    }

    /// <summary>
    /// Exchanges the stored refresh token for a new access token and updates the store.
    /// Returns false (dropping a dead credential) when the grant is rejected, so the next
    /// request fails with the sign-in remediation instead of reusing a corpse.
    /// </summary>
    public async Task<bool> RefreshAsync(string serverName, CancellationToken cancellationToken = default)
    {
        var record = _store.Get(serverName);
        if (record?.RefreshToken is null)
        {
            return false;
        }

        using var response = await PostFormAsync(
            new Uri(record.TokenEndpoint),
            new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = record.RefreshToken,
                ["client_id"] = record.ClientId,
                ["resource"] = record.ServerUrl
            },
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
            {
                _store.Remove(serverName);
            }

            return false;
        }

        var tokens = await ReadTokenResponseAsync(response, cancellationToken).ConfigureAwait(false);
        _store.Save(serverName, record with
        {
            AccessToken = tokens.AccessToken,
            RefreshToken = tokens.RefreshToken ?? record.RefreshToken,
            ExpiresAtUtc = tokens.ExpiresAtUtc ?? record.ExpiresAtUtc
        });
        return true;
    }

    private async Task<AuthorizationServerMetadata> DiscoverAuthorizationServerAsync(
        Uri serverUri,
        CancellationToken cancellationToken)
    {
        // RFC 9728: the protected-resource metadata lives at the origin with the well-known
        // segment inserted before the resource path.
        var resourceMetadata = new Uri(serverUri.GetLeftPart(UriPartial.Authority) +
            "/.well-known/oauth-protected-resource" + serverUri.AbsolutePath);
        string? issuer = null;
        using (var response = await GetAsync(resourceMetadata, cancellationToken).ConfigureAwait(false))
        {
            if (response.IsSuccessStatusCode)
            {
                using var document = JsonDocument.Parse(await ReadContentAsync(response, cancellationToken).ConfigureAwait(false));
                if (document.RootElement.TryGetProperty("authorization_servers", out var servers) &&
                    servers.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in servers.EnumerateArray())
                    {
                        if (entry.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(entry.GetString()))
                        {
                            issuer = entry.GetString();
                            break;
                        }
                    }
                }
            }
        }

        // Servers without resource metadata are assumed to be their own authorization server.
        var issuerUri = new Uri(issuer ?? serverUri.GetLeftPart(UriPartial.Authority), UriKind.Absolute);
        return await ReadAuthorizationServerMetadataAsync(issuerUri, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AuthorizationServerMetadata> ReadAuthorizationServerMetadataAsync(
        Uri issuer,
        CancellationToken cancellationToken)
    {
        var origin = issuer.GetLeftPart(UriPartial.Authority);
        var issuerPath = issuer.AbsolutePath.TrimEnd('/');
        foreach (var wellKnown in new[] { "/.well-known/oauth-authorization-server", "/.well-known/openid-configuration" })
        {
            var metadataUri = new Uri(origin + wellKnown + issuerPath);
            using var response = await GetAsync(metadataUri, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                continue;
            }

            using var document = JsonDocument.Parse(await ReadContentAsync(response, cancellationToken).ConfigureAwait(false));
            var root = document.RootElement;
            if (!TryGetUri(root, "authorization_endpoint", out var authorizationEndpoint) ||
                !TryGetUri(root, "token_endpoint", out var tokenEndpoint))
            {
                continue;
            }

            TryGetUri(root, "registration_endpoint", out var registrationEndpoint);
            return new AuthorizationServerMetadata(authorizationEndpoint!, tokenEndpoint!, registrationEndpoint);
        }

        throw new InvalidOperationException(
            $"Could not discover OAuth authorization server metadata for '{issuer}'.");
    }

    private async Task<string> RegisterClientAsync(
        Uri registrationEndpoint,
        Uri redirectUri,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("client_name", "wfx");
            writer.WriteString("token_endpoint_auth_method", "none");
            writer.WritePropertyName("redirect_uris");
            writer.WriteStartArray();
            writer.WriteStringValue(redirectUri.ToString());
            writer.WriteEndArray();
            writer.WritePropertyName("grant_types");
            writer.WriteStartArray();
            writer.WriteStringValue("authorization_code");
            writer.WriteStringValue("refresh_token");
            writer.WriteEndArray();
            writer.WritePropertyName("response_types");
            writer.WriteStartArray();
            writer.WriteStringValue("code");
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        using var content = new ByteArrayContent(buffer.ToArray());
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, registrationEndpoint) { Content = content }, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Dynamic client registration failed with HTTP {(int)response.StatusCode}.");
        }

        using var document = JsonDocument.Parse(await ReadContentAsync(response, cancellationToken).ConfigureAwait(false));
        if (!document.RootElement.TryGetProperty("client_id", out var clientIdElement) ||
            clientIdElement.ValueKind != JsonValueKind.String ||
            clientIdElement.GetString() is not { Length: > 0 } clientId)
        {
            throw new InvalidOperationException("Dynamic client registration returned no client_id.");
        }

        return clientId;
    }

    private async Task<TokenResponse> ExchangeCodeAsync(
        Uri tokenEndpoint,
        string code,
        string verifier,
        Uri redirectUri,
        string clientId,
        string resource,
        CancellationToken cancellationToken)
    {
        using var response = await PostFormAsync(
            tokenEndpoint,
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri.ToString(),
                ["client_id"] = clientId,
                ["code_verifier"] = verifier,
                ["resource"] = resource
            },
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"The token exchange failed with HTTP {(int)response.StatusCode}.");
        }

        return await ReadTokenResponseAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TokenResponse> ReadTokenResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(await ReadContentAsync(response, cancellationToken).ConfigureAwait(false));
        var root = document.RootElement;
        if (!root.TryGetProperty("access_token", out var accessTokenElement) ||
            accessTokenElement.ValueKind != JsonValueKind.String ||
            accessTokenElement.GetString() is not { Length: > 0 } accessToken)
        {
            throw new InvalidOperationException("The token endpoint returned no access token.");
        }

        string? refreshToken = root.TryGetProperty("refresh_token", out var refreshElement) &&
            refreshElement.ValueKind == JsonValueKind.String
            ? refreshElement.GetString()
            : null;
        DateTimeOffset? expiresAt = root.TryGetProperty("expires_in", out var expiresElement) &&
            expiresElement.ValueKind == JsonValueKind.Number &&
            expiresElement.TryGetInt64(out var seconds)
            ? DateTimeOffset.UtcNow.AddSeconds(seconds)
            : null;
        return new TokenResponse(accessToken, refreshToken, expiresAt);
    }

    private async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken cancellationToken) =>
        await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, uri), cancellationToken).ConfigureAwait(false);

    private async Task<HttpResponseMessage> PostFormAsync(
        Uri uri,
        IReadOnlyDictionary<string, string> fields,
        CancellationToken cancellationToken)
    {
        return await SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new FormUrlEncodedContent(fields)
            };
            request.Headers.Accept.ParseAdd("application/json");
            return request;
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> build,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClient.SendAsync(build(), HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new InvalidOperationException($"The OAuth endpoint could not be reached: {exception.Message}", exception);
        }
    }

    private static async Task<string> ReadContentAsync(HttpResponseMessage response, CancellationToken cancellationToken) =>
        await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

    private static bool TryGetUri(JsonElement root, string property, out Uri? uri)
    {
        uri = null;
        if (root.TryGetProperty(property, out var element) &&
            element.ValueKind == JsonValueKind.String &&
            Uri.TryCreate(element.GetString(), UriKind.Absolute, out var parsed))
        {
            uri = parsed;
        }

        return uri is not null;
    }

    private static Uri BuildAuthorizationUrl(
        AuthorizationServerMetadata metadata,
        string clientId,
        Uri redirectUri,
        string challenge,
        string state,
        string resource)
    {
        var query = new StringBuilder();
        Append(query, "response_type", "code");
        Append(query, "client_id", clientId);
        Append(query, "redirect_uri", redirectUri.ToString());
        Append(query, "code_challenge", challenge);
        Append(query, "code_challenge_method", "S256");
        Append(query, "state", state);
        Append(query, "resource", resource);
        var separator = string.IsNullOrEmpty(metadata.AuthorizationEndpoint.Query) ? '?' : '&';
        return new Uri(metadata.AuthorizationEndpoint + separator.ToString() + query);
    }

    private static void Append(StringBuilder query, string name, string value)
    {
        if (query.Length > 0)
        {
            query.Append('&');
        }

        query.Append(Uri.EscapeDataString(name)).Append('=').Append(Uri.EscapeDataString(value));
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            result[Uri.UnescapeDataString(pair[0].Replace('+', ' '))] =
                pair.Length > 1 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : string.Empty;
        }

        return result;
    }

    private static string NewUrlSafeToken(int byteCount) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(byteCount))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static string ComputeS256(string verifier) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private sealed record AuthorizationServerMetadata(
        Uri AuthorizationEndpoint,
        Uri TokenEndpoint,
        Uri? RegistrationEndpoint);

    private sealed record TokenResponse(string AccessToken, string? RefreshToken, DateTimeOffset? ExpiresAtUtc);
}

/// <summary>
/// The interactive redirect: reserves a loopback port, opens the system browser at the
/// authorization URL, and serves one callback request with a "you can close this window"
/// page. A bare TCP listener is used instead of <see cref="HttpListener"/> because http.sys
/// requires URL ACL reservations an unprivileged user does not have.
/// </summary>
public sealed class McpLoopbackBrowserRedirect : IMcpAuthorizationRedirect, IDisposable
{
    private readonly TcpListener _listener;
    private readonly Action<Uri> _openBrowser;

    /// <param name="openBrowser">Overrides the system-browser launch; tests pass a no-op and drive
    /// the loopback callback with an HTTP client instead of opening a real browser.</param>
    public McpLoopbackBrowserRedirect(Action<Uri>? openBrowser = null)
    {
        _openBrowser = openBrowser ?? OpenBrowser;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        RedirectUri = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/callback");
    }

    public Uri RedirectUri { get; }

    public async Task<Uri> WaitForCallbackAsync(Uri authorizationUrl, CancellationToken cancellationToken)
    {
        _openBrowser(authorizationUrl);
        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                _listener.Stop();
            }
            catch (ObjectDisposedException)
            {
            }
        });
        using var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
        var stream = client.GetStream();
        var requestLine = await ReadRequestLineAsync(stream, cancellationToken).ConfigureAwait(false);
        var path = requestLine.Split(' ') is { Length: >= 2 } parts ? parts[1] : "/";
        var body = Encoding.UTF8.GetBytes(
            "<html><body><p>wfx: sign-in complete. You can close this window.</p></body></html>");
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        // A bare Dispose would RST the socket and could discard the buffered page before the
        // browser reads it; shut down gracefully first.
        client.Client.Shutdown(SocketShutdown.Send);
        return new Uri($"http://127.0.0.1:{RedirectUri.Port}{path}");
    }

    public void Dispose() => _listener.Stop();

    private static void OpenBrowser(Uri url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url.ToString())
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // No browser available; the CLI prints the URL so the user can open it manually.
        }
    }

    private static async Task<string> ReadRequestLineAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var buffer = new byte[1];
        while (builder.Length < 16 * 1024)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var ch = (char)buffer[0];
            if (ch == '\n')
            {
                break;
            }

            if (ch != '\r')
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }
}
