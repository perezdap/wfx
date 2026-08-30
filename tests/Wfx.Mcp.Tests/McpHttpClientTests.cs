using System.Text.Json;
using Wfx.Core;
using Wfx.Mcp;

using Wfx.Testing;

namespace Wfx.Mcp.Tests;

public sealed class McpHttpClientTests
{
    /// <summary>Routes a JSON-RPC POST to a per-method responder; notifications get 202.</summary>
    private static Func<LoopbackRequest, LoopbackResponse> Handler(
        Func<string, JsonElement, LoopbackResponse> respond) => request =>
    {
        using var document = JsonDocument.Parse(request.Body);
        var root = document.RootElement;
        if (!root.TryGetProperty("id", out _))
        {
            return LoopbackResponse.Accepted();
        }

        return respond(root.GetProperty("method").GetString()!, root);
    };

    private static LoopbackResponse Result(JsonElement request, string resultJson) =>
        LoopbackResponse.Json(
            $"{{\"jsonrpc\":\"2.0\",\"id\":{request.GetProperty("id").GetRawText()},\"result\":{resultJson}}}");

    private static LoopbackResponse InitializeResult(JsonElement request) =>
        Result(request, "{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"serverInfo\":{\"name\":\"fake\",\"version\":\"1.0\"}}");


    [Fact]
    public async Task InitializeAsync_PostsJsonRpcAndReadsJsonResponse()
    {
        using var server = new LoopbackHttpServer(request =>
        {
            using var document = JsonDocument.Parse(request.Body);
            if (!document.RootElement.TryGetProperty("id", out var id))
            {
                return LoopbackResponse.Accepted();
            }

            return LoopbackResponse.Json(
                $"{{\"jsonrpc\":\"2.0\",\"id\":{id.GetRawText()}," +
                "\"result\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"serverInfo\":{\"name\":\"fake\",\"version\":\"1.0\"}}}");
        });
        var settings = McpServerSettings.ForHttp(new Uri(server.BaseUri, "/mcp").ToString());

        await using var client = McpHttpClient.Start(settings, "remote", cancellationToken: TestContext.Current.CancellationToken);
        await client.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Null(server.LastError);
        var initialize = server.Requests[0];
        Assert.Equal("POST", initialize.Method);
        Assert.Equal("/mcp", initialize.Path);
        Assert.Contains("application/json", initialize.Headers["Accept"]);
        Assert.Contains("text/event-stream", initialize.Headers["Accept"]);
        // The initialized notification is accepted without a body.
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task InitializeAsync_ReadsServerSentEventResponse()
    {
        using var server = new LoopbackHttpServer(request =>
        {
            using var document = JsonDocument.Parse(request.Body);
            if (!document.RootElement.TryGetProperty("id", out var id))
            {
                return LoopbackResponse.Accepted();
            }

            var response = $"{{\"jsonrpc\":\"2.0\",\"id\":{id.GetRawText()}," +
                "\"result\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"serverInfo\":{\"name\":\"fake\",\"version\":\"1.0\"}}}";
            return new LoopbackResponse(200, "text/event-stream", $"event: message\ndata: {response}\n\n");
        });
        var settings = McpServerSettings.ForHttp(new Uri(server.BaseUri, "/mcp").ToString());

        await using var client = McpHttpClient.Start(settings, "remote", cancellationToken: TestContext.Current.CancellationToken);
        await client.InitializeAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SessionIdAndProtocolVersion_AreSentAfterInitialize()
    {
        using var server = new LoopbackHttpServer(Handler((method, request) =>
        {
            if (method == "initialize")
            {
                var response = InitializeResult(request);
                return response with
                {
                    ExtraHeaders = new Dictionary<string, string> { ["Mcp-Session-Id"] = "session-abc" }
                };
            }

            return Result(request, "{\"tools\":[]}");
        }));
        var settings = McpServerSettings.ForHttp(new Uri(server.BaseUri, "/mcp").ToString());

        await using var client = McpHttpClient.Start(settings, "remote", cancellationToken: TestContext.Current.CancellationToken);
        await client.InitializeAsync(TestContext.Current.CancellationToken);
        await client.ListToolsAsync(TestContext.Current.CancellationToken);

        var initialize = server.Requests[0];
        Assert.False(initialize.Headers.ContainsKey("Mcp-Session-Id"));
        var list = Assert.Single(server.Requests, request => request.Body.Contains("tools/list", StringComparison.Ordinal));
        Assert.Equal("session-abc", list.Headers["Mcp-Session-Id"]);
        Assert.Equal(McpProtocolClient.OfferedProtocolVersion, list.Headers["MCP-Protocol-Version"]);
    }

    [Fact]
    public async Task ListToolsAsync_PaginatesAcrossPosts()
    {
        using var server = new LoopbackHttpServer(Handler((method, request) =>
        {
            if (method == "initialize")
            {
                return InitializeResult(request);
            }

            var hasCursor = request.TryGetProperty("params", out var parameters) &&
                parameters.TryGetProperty("cursor", out _);
            return hasCursor
                ? Result(request, "{\"tools\":[{\"name\":\"second\"}]}")
                : Result(request, "{\"tools\":[{\"name\":\"first\",\"inputSchema\":{\"type\":\"object\"}}],\"nextCursor\":\"page2\"}");
        }));
        var settings = McpServerSettings.ForHttp(new Uri(server.BaseUri, "/mcp").ToString());

        await using var client = McpHttpClient.Start(settings, "remote", cancellationToken: TestContext.Current.CancellationToken);
        await client.InitializeAsync(TestContext.Current.CancellationToken);
        var tools = await client.ListToolsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "first", "second" }, tools.Select(tool => tool.Name));
    }

    [Fact]
    public async Task CallToolAsync_RoundTrips()
    {
        using var server = new LoopbackHttpServer(Handler((method, request) =>
        {
            if (method == "initialize")
            {
                return InitializeResult(request);
            }

            var arguments = request.GetProperty("params").GetProperty("arguments");
            return Result(request,
                $"{{\"content\":[{{\"type\":\"text\",\"text\":\"echo:{arguments.GetProperty("text").GetString()}\"}}]}}");
        }));
        var settings = McpServerSettings.ForHttp(new Uri(server.BaseUri, "/mcp").ToString());

        await using var client = McpHttpClient.Start(settings, "remote", cancellationToken: TestContext.Current.CancellationToken);
        await client.InitializeAsync(TestContext.Current.CancellationToken);
        using var arguments = JsonDocument.Parse("{\"text\":\"hello\"}");
        var result = await client.CallToolAsync("echo", arguments.RootElement, TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        Assert.Equal("echo:hello", result.Output);
    }

    [Fact]
    public async Task MalformedResponse_FaultsTheSession()
    {
        using var server = new LoopbackHttpServer(Handler((method, request) =>
            method == "initialize"
                ? InitializeResult(request)
                : LoopbackResponse.Json("this is not json")));
        var settings = McpServerSettings.ForHttp(new Uri(server.BaseUri, "/mcp").ToString());

        await using var client = McpHttpClient.Start(settings, "remote", cancellationToken: TestContext.Current.CancellationToken);
        await client.InitializeAsync(TestContext.Current.CancellationToken);
        var exception = await Assert.ThrowsAsync<McpConnectionException>(
            () => client.ListToolsAsync(TestContext.Current.CancellationToken));

        Assert.Contains("malformed", exception.Message);
    }

    [Fact]
    public async Task NonSuccessStatusMidCall_FailsTheCallStructurally()
    {
        var calls = 0;
        using var server = new LoopbackHttpServer(Handler((method, request) =>
        {
            if (method == "initialize")
            {
                return InitializeResult(request);
            }

            calls++;
            return LoopbackResponse.Json("{\"detail\":\"boom\"}", status: 500);
        }));
        var settings = McpServerSettings.ForHttp(new Uri(server.BaseUri, "/mcp").ToString());

        await using var client = McpHttpClient.Start(settings, "remote", cancellationToken: TestContext.Current.CancellationToken);
        await client.InitializeAsync(TestContext.Current.CancellationToken);
        var exception = await Assert.ThrowsAsync<McpConnectionException>(
            () => client.ListToolsAsync(TestContext.Current.CancellationToken));

        Assert.Contains("HTTP 500", exception.Message);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Unauthorized_ThrowsSignInRemediation()
    {
        using var server = new LoopbackHttpServer(_ => LoopbackResponse.Json("{}", status: 401));
        var settings = McpServerSettings.ForHttp(new Uri(server.BaseUri, "/mcp").ToString());

        await using var client = McpHttpClient.Start(settings, "remote", cancellationToken: TestContext.Current.CancellationToken);
        var exception = await Assert.ThrowsAsync<McpAuthorizationException>(
            () => client.InitializeAsync(TestContext.Current.CancellationToken));

        Assert.Contains("wfx mcp auth remote", exception.Message);
    }

    [Fact]
    public async Task Cancellation_CancelsTheInFlightRequest()
    {
        using var server = new LoopbackHttpServer(async (request, cancellationToken) =>
        {
            using var document = JsonDocument.Parse(request.Body);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out _))
            {
                return LoopbackResponse.Accepted();
            }

            if (root.GetProperty("method").GetString() == "initialize")
            {
                return InitializeResult(root);
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            return LoopbackResponse.Accepted();
        });
        var settings = McpServerSettings.ForHttp(new Uri(server.BaseUri, "/mcp").ToString());

        await using var client = McpHttpClient.Start(settings, "remote", cancellationToken: TestContext.Current.CancellationToken);
        await client.InitializeAsync(TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        using var arguments = JsonDocument.Parse("{}");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.CallToolAsync("echo", arguments.RootElement, cancellation.Token));
    }

    [Fact]
    public async Task StoredToken_IsSentAsBearer()
    {
        using var server = new LoopbackHttpServer(Handler((method, request) =>
            method == "initialize" ? InitializeResult(request) : Result(request, "{\"tools\":[]}")));
        using var directory = new TemporaryDirectory();
        var store = new McpTokenStore(Path.Combine(directory.Path, "mcp-tokens.json"));
        store.Save("remote", new McpTokenRecord(
            "https://mcp.example.com/mcp", "stored-access-token", null,
            DateTimeOffset.UtcNow.AddHours(1), "https://auth.example.com/token", "wfx"));
        var settings = McpServerSettings.ForHttp(new Uri(server.BaseUri, "/mcp").ToString());

        await using var client = McpHttpClient.Start(
            settings, "remote", tokenStore: store, cancellationToken: TestContext.Current.CancellationToken);
        await client.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Bearer stored-access-token", server.Requests[0].Headers["Authorization"]);
    }

    [Fact]
    public async Task ExpiredToken_IsRefreshedBeforeSending()
    {
        using var authorizationServer = new LoopbackHttpServer(request =>
        {
            Assert.Equal("/token", request.Path);
            return LoopbackResponse.Json(
                """{"access_token":"fresh-access-token","refresh_token":"refresh-2","expires_in":3600,"token_type":"Bearer"}""");
        });
        using var server = new LoopbackHttpServer(Handler((method, request) =>
            method == "initialize" ? InitializeResult(request) : Result(request, "{\"tools\":[]}")));
        using var directory = new TemporaryDirectory();
        var store = new McpTokenStore(Path.Combine(directory.Path, "mcp-tokens.json"));
        store.Save("remote", new McpTokenRecord(
            new Uri(server.BaseUri, "/mcp").ToString(), "stale-access-token", "refresh-1",
            DateTimeOffset.UtcNow.AddMinutes(-5),
            new Uri(authorizationServer.BaseUri, "/token").ToString(), "wfx"));
        var settings = McpServerSettings.ForHttp(new Uri(server.BaseUri, "/mcp").ToString());

        await using var client = McpHttpClient.Start(
            settings, "remote", tokenStore: store, cancellationToken: TestContext.Current.CancellationToken);
        await client.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Bearer fresh-access-token", server.Requests[0].Headers["Authorization"]);
        Assert.Equal("fresh-access-token", store.Get("remote")!.AccessToken);
    }

    [Fact]
    public async Task Unauthorized_WithDeadRefreshToken_RemediatesAndDropsCredential()
    {
        using var authorizationServer = new LoopbackHttpServer(_ =>
            LoopbackResponse.Json("{\"error\":\"invalid_grant\"}", status: 400));
        using var server = new LoopbackHttpServer(_ => LoopbackResponse.Json("{}", status: 401));
        using var directory = new TemporaryDirectory();
        var store = new McpTokenStore(Path.Combine(directory.Path, "mcp-tokens.json"));
        store.Save("remote", new McpTokenRecord(
            new Uri(server.BaseUri, "/mcp").ToString(), "stale-access-token", "dead-refresh",
            DateTimeOffset.UtcNow.AddHours(1),
            new Uri(authorizationServer.BaseUri, "/token").ToString(), "wfx"));
        var settings = McpServerSettings.ForHttp(new Uri(server.BaseUri, "/mcp").ToString());

        await using var client = McpHttpClient.Start(
            settings, "remote", tokenStore: store, cancellationToken: TestContext.Current.CancellationToken);
        var exception = await Assert.ThrowsAsync<McpAuthorizationException>(
            () => client.InitializeAsync(TestContext.Current.CancellationToken));

        Assert.Contains("wfx mcp auth remote", exception.Message);
        Assert.Null(store.Get("remote"));
    }

    [Fact]
    public async Task StoredToken_TakesPrecedenceOverConfiguredAuthorizationHeader()
    {
        using var server = new LoopbackHttpServer(Handler((method, request) =>
            method == "initialize" ? InitializeResult(request) : Result(request, "{\"tools\":[]}")));
        using var directory = new TemporaryDirectory();
        var store = new McpTokenStore(Path.Combine(directory.Path, "mcp-tokens.json"));
        store.Save("remote", new McpTokenRecord(
            "https://mcp.example.com/mcp", "oauth-token", null,
            DateTimeOffset.UtcNow.AddHours(1), "https://auth.example.com/token", "wfx"));
        var settings = McpServerSettings.ForHttp(
            new Uri(server.BaseUri, "/mcp").ToString(),
            new Dictionary<string, string> { ["Authorization"] = "Bearer static-config-token" });

        await using var client = McpHttpClient.Start(
            settings, "remote", tokenStore: store, cancellationToken: TestContext.Current.CancellationToken);
        await client.InitializeAsync(TestContext.Current.CancellationToken);

        // Exactly one Authorization header: the OAuth credential wins over the static config.
        Assert.Equal("Bearer oauth-token", server.Requests[0].Headers["Authorization"]);
    }

    [Fact]
    public async Task ConfiguredHeaders_AreSent_AndNeverLeakIntoErrors()
    {
        using var server = new LoopbackHttpServer(_ =>
            LoopbackResponse.Json("{\"detail\":\"boom\"}", status: 500));
        var settings = McpServerSettings.ForHttp(
            new Uri(server.BaseUri, "/mcp").ToString(),
            new Dictionary<string, string> { ["X-Api-Key"] = "super-secret-value" });

        await using var client = McpHttpClient.Start(settings, "remote", cancellationToken: TestContext.Current.CancellationToken);
        var exception = await Assert.ThrowsAsync<McpConnectionException>(
            () => client.InitializeAsync(TestContext.Current.CancellationToken));

        Assert.Equal("super-secret-value", server.Requests[0].Headers["X-Api-Key"]);
        Assert.DoesNotContain("super-secret-value", exception.Message);
        Assert.DoesNotContain("super-secret-value", exception.ToString());
    }
}
