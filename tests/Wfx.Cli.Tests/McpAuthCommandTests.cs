using System.Text.Json;
using Wfx.Core;
using Wfx.Testing;

namespace Wfx.Cli.Tests;

[Collection("Console")]
public sealed class McpAuthCommandTests
{
    [Fact]
    public async Task Auth_CompletesPkceSignIn_AndPersistsCredential()
    {
        var directory = Directory.CreateTempSubdirectory("wfx-cli-tests-");
        using var httpClient = CliRunner.CreateUnexpectedHttpClient(
            "MCP sign-in runs on the host's own client, never the CLI's.");
        using var console = new ConsoleCapture();
        string? codeChallenge = null;
        Dictionary<string, string>? tokenForm = null;
        LoopbackHttpServer? authorizationServer = null;
        LoopbackResponse HandleAuthorization(LoopbackRequest request)
        {
            var baseText = authorizationServer!.BaseUri.ToString().TrimEnd('/');
            switch (request.Path)
            {
                case "/.well-known/oauth-authorization-server":
                    // No registration_endpoint: the fixed public client id path.
                    return LoopbackResponse.Json($$"""
                        {"issuer":"{{baseText}}","authorization_endpoint":"{{baseText}}/authorize","token_endpoint":"{{baseText}}/token"}
                        """);
                case "/authorize":
                    var query = LoopbackOAuth.ParseQuery(request.Query);
                    codeChallenge = query["code_challenge"];
                    return new LoopbackResponse(302, "text/plain", string.Empty,
                        new Dictionary<string, string>
                        {
                            ["Location"] = $"{query["redirect_uri"]}?code=authcode-1&state={query["state"]}"
                        });
                case "/token":
                    tokenForm = LoopbackOAuth.ParseForm(request.Body);
                    return LoopbackResponse.Json(
                        """{"access_token":"access-1","refresh_token":"refresh-1","expires_in":3600,"token_type":"Bearer"}""");
                default:
                    return LoopbackResponse.Json("{\"error\":\"not_found\"}", status: 404);
            }
        }

        authorizationServer = new LoopbackHttpServer(HandleAuthorization);
        using var authorization = authorizationServer;
        using var resource = new LoopbackHttpServer(request =>
            request.Path == "/.well-known/oauth-protected-resource/mcp"
                ? LoopbackResponse.Json($$"""
                    {"resource":"mcp","authorization_servers":["{{authorizationServer.BaseUri}}"]}
                    """)
                : LoopbackResponse.Json("{}", status: 404));
        try
        {
            Directory.CreateDirectory(Path.Combine(directory.FullName, ".wfx"));
            File.WriteAllText(Path.Combine(directory.FullName, ".wfx", "config.json"), $$"""
                { "mcp_servers": { "remote": { "url": "{{new Uri(resource.BaseUri, "/mcp")}}" } } }
                """);

            var urlReady = new TaskCompletionSource<Uri>();
            var runTask = CliRunner.RunAsync(
                ["mcp", "auth", "remote"],
                httpClient,
                new SessionStore(Path.Combine(directory.FullName, "sessions")),
                TestContext.Current.CancellationToken,
                userProfile: directory.FullName,
                openBrowser: urlReady.SetResult);
            var authorizationUrl = await urlReady.Task.WaitAsync(
                TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            // The headless "browser": sign in at the authorization server and follow its
            // redirect to the loopback listener the CLI is holding open.
            using var browser = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
            using (var signIn = await browser.GetAsync(authorizationUrl, TestContext.Current.CancellationToken))
            {
                Assert.Equal(302, (int)signIn.StatusCode);
                using var callback = await browser.GetAsync(signIn.Headers.Location, TestContext.Current.CancellationToken);
                Assert.Contains(
                    "sign-in complete",
                    await callback.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            }

            Assert.Equal(0, await runTask);
            Assert.Contains("signed in to MCP server 'remote'", console.Output.ToString());
            var storeText = File.ReadAllText(Path.Combine(directory.FullName, ".wfx", "mcp-tokens.json"));
            using var document = JsonDocument.Parse(storeText);
            var remote = document.RootElement.GetProperty("servers").GetProperty("remote");
            Assert.Equal("access-1", remote.GetProperty("access_token").GetString());
            Assert.Equal("refresh-1", remote.GetProperty("refresh_token").GetString());
            Assert.Equal("wfx", remote.GetProperty("client_id").GetString());
            // PKCE held end to end, and the token endpoint saw the fixed public client id.
            Assert.NotNull(tokenForm);
            Assert.Equal("authorization_code", tokenForm!["grant_type"]);
            Assert.Equal("authcode-1", tokenForm["code"]);
            Assert.Equal("wfx", tokenForm["client_id"]);
            Assert.Equal(LoopbackOAuth.S256(tokenForm["code_verifier"]), codeChallenge);
            // Token material never reaches the console.
            Assert.DoesNotContain("access-1", console.Output.ToString());
            Assert.DoesNotContain("access-1", console.ErrorText);
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task Revoke_RemovesStoredCredential()
    {
        var directory = Directory.CreateTempSubdirectory("wfx-cli-tests-");
        using var httpClient = CliRunner.CreateUnexpectedHttpClient(
            "Revoking an MCP credential must not call any endpoint.");
        using var console = new ConsoleCapture();
        try
        {
            Directory.CreateDirectory(Path.Combine(directory.FullName, ".wfx"));
            var storePath = Path.Combine(directory.FullName, ".wfx", "mcp-tokens.json");
            File.WriteAllText(storePath, """
                {"servers":{"remote":{"server_url":"https://mcp.example.com/mcp","access_token":"access-1","refresh_token":"refresh-1","token_endpoint":"https://auth.example.com/token","client_id":"wfx"}}}
                """);

            var exitCode = await CliRunner.RunAsync(
                ["mcp", "auth", "--revoke", "remote"],
                httpClient,
                new SessionStore(Path.Combine(directory.FullName, "sessions")),
                TestContext.Current.CancellationToken,
                userProfile: directory.FullName);

            Assert.Equal(0, exitCode);
            Assert.DoesNotContain("access-1", File.ReadAllText(storePath));
            Assert.Contains("removed the stored credential", console.Output.ToString());
            Assert.DoesNotContain("access-1", console.Output.ToString());
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task Revoke_WithoutStoredCredential_StillSucceeds()
    {
        var directory = Directory.CreateTempSubdirectory("wfx-cli-tests-");
        using var httpClient = CliRunner.CreateUnexpectedHttpClient(
            "Revoking an MCP credential must not call any endpoint.");
        using var console = new ConsoleCapture();
        try
        {
            var exitCode = await CliRunner.RunAsync(
                ["mcp", "auth", "--revoke", "ghost"],
                httpClient,
                new SessionStore(Path.Combine(directory.FullName, "sessions")),
                TestContext.Current.CancellationToken,
                userProfile: directory.FullName);

            Assert.Equal(0, exitCode);
            Assert.Contains("no stored credential", console.Output.ToString());
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task Auth_UnknownServer_IsAUsageError()
    {
        var directory = Directory.CreateTempSubdirectory("wfx-cli-tests-");
        using var httpClient = CliRunner.CreateUnexpectedHttpClient(
            "An unknown MCP server must not call any endpoint.");
        using var console = new ConsoleCapture();
        try
        {
            var exitCode = await CliRunner.RunAsync(
                ["mcp", "auth", "ghost"],
                httpClient,
                new SessionStore(Path.Combine(directory.FullName, "sessions")),
                TestContext.Current.CancellationToken,
                userProfile: directory.FullName);

            Assert.Equal(2, exitCode);
            Assert.Contains("'ghost'", console.ErrorText);
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task Auth_StdioServer_IsRejected()
    {
        var directory = Directory.CreateTempSubdirectory("wfx-cli-tests-");
        using var httpClient = CliRunner.CreateUnexpectedHttpClient(
            "A stdio MCP server must not call any endpoint.");
        using var console = new ConsoleCapture();
        try
        {
            Directory.CreateDirectory(Path.Combine(directory.FullName, ".wfx"));
            File.WriteAllText(Path.Combine(directory.FullName, ".wfx", "config.json"), """
                { "mcp_servers": { "local": { "command": "node" } } }
                """);

            var exitCode = await CliRunner.RunAsync(
                ["mcp", "auth", "local"],
                httpClient,
                new SessionStore(Path.Combine(directory.FullName, "sessions")),
                TestContext.Current.CancellationToken,
                userProfile: directory.FullName);

            Assert.Equal(2, exitCode);
            Assert.Contains("stdio", console.ErrorText);
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }
}
