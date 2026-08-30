using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Wfx.Core;
using Wfx.Mcp;

namespace Wfx.Mcp.Tests;

public sealed class McpOAuthFlowTests
{
    /// <summary>
    /// A scripted authorization server: metadata discovery, dynamic client registration, an
    /// authorize endpoint that immediately redirects with a code (standing in for the user's
    /// browser sign-in), and a token endpoint that verifies the PKCE verifier against the
    /// challenge it saw.
    /// </summary>
    private sealed class MockAuthorizationServer : IDisposable
    {
        private readonly LoopbackHttpServer _server;

        public MockAuthorizationServer()
        {
            _server = new LoopbackHttpServer(Handle);
        }

        public string? SeenCodeChallenge { get; private set; }

        public string? SeenState { get; private set; }

        public string? RegisteredClientId { get; private set; }

        public int TokenCalls { get; private set; }

        public Uri BaseUri => _server.BaseUri;

        public string BaseText => BaseUri.ToString().TrimEnd('/');

        public IReadOnlyList<LoopbackRequest> Requests => _server.Requests;

        public Exception? LastError => _server.LastError;

        public void Dispose() => _server.Dispose();

        private LoopbackResponse Handle(LoopbackRequest request) => request.Path switch
        {
            "/.well-known/oauth-authorization-server" => LoopbackResponse.Json($$"""
                {"issuer":"{{BaseText}}","authorization_endpoint":"{{BaseText}}/authorize","token_endpoint":"{{BaseText}}/token","registration_endpoint":"{{BaseText}}/register"}
                """),
            "/register" => Register(request),
            "/authorize" => Authorize(request),
            "/token" => Token(request),
            _ => LoopbackResponse.Json("{\"error\":\"not_found\"}", status: 404)
        };

        private LoopbackResponse Register(LoopbackRequest request)
        {
            RegisteredClientId = "dcr-client-1";
            return LoopbackResponse.Json($$"""{"client_id":"{{RegisteredClientId}}","client_name":"wfx"}""");
        }

        private LoopbackResponse Authorize(LoopbackRequest request)
        {
            var query = ParseQuery(request.Query);
            SeenCodeChallenge = query["code_challenge"];
            SeenState = query["state"];
            Assert.Equal("S256", query["code_challenge_method"]);
            Assert.Equal("code", query["response_type"]);
            var location = $"{query["redirect_uri"]}?code=authcode-1&state={query["state"]}";
            return new LoopbackResponse(302, "text/plain", string.Empty,
                new Dictionary<string, string> { ["Location"] = location });
        }

        private LoopbackResponse Token(LoopbackRequest request)
        {
            TokenCalls++;
            var form = ParseForm(request.Body);
            if (form["grant_type"] == "refresh_token")
            {
                if (form["refresh_token"] != "refresh-1")
                {
                    return LoopbackResponse.Json("{\"error\":\"invalid_grant\"}", status: 400);
                }

                return LoopbackResponse.Json(
                    """{"access_token":"access-2","refresh_token":"refresh-2","expires_in":3600,"token_type":"Bearer"}""");
            }

            Assert.Equal("authorization_code", form["grant_type"]);
            Assert.Equal("authcode-1", form["code"]);
            Assert.Equal(S256(form["code_verifier"]), SeenCodeChallenge);
            return LoopbackResponse.Json(
                """{"access_token":"access-1","refresh_token":"refresh-1","expires_in":3600,"token_type":"Bearer"}""");
        }
    }

    /// <summary>Headless stand-in for the browser: GETs the authorize URL and returns the redirect.</summary>
    private sealed class FakeRedirect : IMcpAuthorizationRedirect
    {
        private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false });

        public FakeRedirect() => RedirectUri = new Uri("http://127.0.0.1:9/callback");

        public Uri RedirectUri { get; }

        public async Task<Uri> WaitForCallbackAsync(Uri authorizationUrl, CancellationToken cancellationToken)
        {
            using var response = await _http.GetAsync(authorizationUrl, cancellationToken);
            Assert.Equal(302, (int)response.StatusCode);
            return new Uri(response.Headers.Location!.ToString());
        }
    }

    [Fact]
    public async Task AuthorizeAsync_CompletesPkceFlow_AndStoresCredential()
    {
        using var authorizationServer = new MockAuthorizationServer();
        using var resource = new LoopbackHttpServer(request =>
            request.Path == "/.well-known/oauth-protected-resource/mcp"
                ? LoopbackResponse.Json($$"""
                    {"resource":"mcp","authorization_servers":["{{authorizationServer.BaseUri}}"]}
                    """)
                : LoopbackResponse.Json("{}", status: 404));
        using var directory = new TemporaryDirectory();
        var store = new McpTokenStore(Path.Combine(directory.Path, "mcp-tokens.json"));
        var serverUrl = new Uri(resource.BaseUri, "/mcp").ToString();
        var flow = new McpOAuthFlow(new HttpClient(), store);
        var redirect = new FakeRedirect();

        await flow.AuthorizeAsync(
            "remote",
            McpServerSettings.ForHttp(serverUrl),
            redirect,
            cancellationToken: TestContext.Current.CancellationToken);

        var record = store.Get("remote");
        Assert.NotNull(record);
        Assert.Equal("access-1", record!.AccessToken);
        Assert.Equal("refresh-1", record.RefreshToken);
        Assert.Equal(serverUrl, record.ServerUrl);
        Assert.Equal(new Uri(authorizationServer.BaseUri, "/token").ToString(), record.TokenEndpoint);
        Assert.Equal("dcr-client-1", record.ClientId);
        Assert.True(record.ExpiresAtUtc > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task RefreshAsync_ExchangesRefreshToken_AndUpdatesStore()
    {
        using var authorizationServer = new MockAuthorizationServer();
        using var directory = new TemporaryDirectory();
        var store = new McpTokenStore(Path.Combine(directory.Path, "mcp-tokens.json"));
        store.Save("remote", new McpTokenRecord(
            "https://mcp.example.com/mcp",
            "access-1",
            "refresh-1",
            DateTimeOffset.UtcNow.AddMinutes(-5),
            new Uri(authorizationServer.BaseUri, "/token").ToString(),
            "dcr-client-1"));
        var flow = new McpOAuthFlow(new HttpClient(), store);

        var refreshed = await flow.RefreshAsync("remote", TestContext.Current.CancellationToken);

        Assert.True(refreshed);
        Assert.Equal("access-2", store.Get("remote")!.AccessToken);
        Assert.Equal("refresh-2", store.Get("remote")!.RefreshToken);
    }

    [Fact]
    public async Task RefreshAsync_DeadGrant_DropsCredentialAndReturnsFalse()
    {
        using var authorizationServer = new MockAuthorizationServer();
        using var directory = new TemporaryDirectory();
        var store = new McpTokenStore(Path.Combine(directory.Path, "mcp-tokens.json"));
        store.Save("remote", new McpTokenRecord(
            "https://mcp.example.com/mcp",
            "access-1",
            "wrong-refresh-token",
            DateTimeOffset.UtcNow.AddMinutes(-5),
            new Uri(authorizationServer.BaseUri, "/token").ToString(),
            "dcr-client-1"));
        var flow = new McpOAuthFlow(new HttpClient(), store);

        var refreshed = await flow.RefreshAsync("remote", TestContext.Current.CancellationToken);

        Assert.False(refreshed);
        Assert.Null(store.Get("remote"));
    }

    [Fact]
    public async Task AuthorizeAsync_StateMismatch_RefusesExchange()
    {
        using var authorizationServer = new MockAuthorizationServer();
        using var resource = new LoopbackHttpServer(request =>
            request.Path == "/.well-known/oauth-protected-resource/mcp"
                ? LoopbackResponse.Json($$"""{"resource":"mcp","authorization_servers":["{{authorizationServer.BaseUri}}"]}""")
                : LoopbackResponse.Json("{}", status: 404));
        using var directory = new TemporaryDirectory();
        var store = new McpTokenStore(Path.Combine(directory.Path, "mcp-tokens.json"));
        var flow = new McpOAuthFlow(new HttpClient(), store);
        var redirect = new TamperingRedirect();

        await Assert.ThrowsAsync<InvalidOperationException>(() => flow.AuthorizeAsync(
            "remote",
            McpServerSettings.ForHttp(new Uri(resource.BaseUri, "/mcp").ToString()),
            redirect,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(0, authorizationServer.TokenCalls);
        Assert.Null(store.Get("remote"));
    }

    [Fact]
    public async Task AuthorizeAsync_OverRealLoopbackListener_PersistsToken()
    {
        using var authorizationServer = new MockAuthorizationServer();
        using var resource = new LoopbackHttpServer(request =>
            request.Path == "/.well-known/oauth-protected-resource/mcp"
                ? LoopbackResponse.Json($$"""{"resource":"mcp","authorization_servers":["{{authorizationServer.BaseUri}}"]}""")
                : LoopbackResponse.Json("{}", status: 404));
        using var directory = new TemporaryDirectory();
        var store = new McpTokenStore(Path.Combine(directory.Path, "mcp-tokens.json"));
        var flow = new McpOAuthFlow(new HttpClient(), store);

        // The "browser": capture the authorization URL the flow would open, sign in at the
        // mock authorization server, and follow its redirect to the loopback listener.
        Uri? authorizationUrl = null;
        using var redirect = new McpLoopbackBrowserRedirect(url => authorizationUrl = url);
        using var browser = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var authorizeTask = flow.AuthorizeAsync(
            "remote",
            McpServerSettings.ForHttp(new Uri(resource.BaseUri, "/mcp").ToString()),
            redirect,
            cancellationToken: TestContext.Current.CancellationToken);

        while (authorizationUrl is null)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        using (var signIn = await browser.GetAsync(authorizationUrl, TestContext.Current.CancellationToken))
        {
            Assert.Equal(302, (int)signIn.StatusCode);
            using var callback = await browser.GetAsync(signIn.Headers.Location, TestContext.Current.CancellationToken);
            Assert.Contains("sign-in complete", await callback.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        await authorizeTask.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal("access-1", store.Get("remote")!.AccessToken);
    }

    private sealed class TamperingRedirect : IMcpAuthorizationRedirect
    {
        public Uri RedirectUri { get; } = new Uri("http://127.0.0.1:9/callback");

        public Task<Uri> WaitForCallbackAsync(Uri authorizationUrl, CancellationToken cancellationToken) =>
            Task.FromResult(new Uri("http://127.0.0.1:9/callback?code=authcode-1&state=forged-state"));
    }

    private static string S256(string verifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static Dictionary<string, string> ParseForm(string body) =>
        body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(
                parts => Uri.UnescapeDataString(parts[0].Replace('+', ' ')),
                parts => parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : string.Empty);

    private static Dictionary<string, string> ParseQuery(string query) => ParseForm(query.TrimStart('?'));
}
