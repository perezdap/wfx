using System.Runtime.CompilerServices;
using System.Text.Json;
using Wfx.Core;
using Wfx.Testing;

namespace Wfx.Cli.Tests;

/// <summary>
/// Acceptance-level coverage for issue #73: a configured HTTP MCP server surfaces its tools in
/// a noninteractive run, and calling one streams the same tool events as a built-in.
/// </summary>
[Collection("Console")]
public sealed class McpHttpRunTests
{
    [Fact]
    public async Task JsonRun_ExposesAndCallsHttpMcpTool()
    {
        var workspace = Directory.CreateTempSubdirectory("wfx-cli-tests-");
        using var console = new ConsoleCapture();
        var originalDirectory = Environment.CurrentDirectory;
        var userProfile = Path.Combine(workspace.FullName, "profile");
        using var mcpServer = new LoopbackHttpServer(request =>
        {
            using var document = JsonDocument.Parse(request.Body);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var id))
            {
                return LoopbackResponse.Accepted();
            }

            var result = root.GetProperty("method").GetString() switch
            {
                "initialize" => "{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"serverInfo\":{\"name\":\"fake\",\"version\":\"1.0\"}}",
                "tools/list" => "{\"tools\":[{\"name\":\"echo\",\"inputSchema\":{\"type\":\"object\"}}]}",
                _ => "{\"content\":[{\"type\":\"text\",\"text\":\"remote-pong\"}]}"
            };
            return LoopbackResponse.Json(
                $"{{\"jsonrpc\":\"2.0\",\"id\":{id.GetRawText()},\"result\":{result}}}");
        });
        Directory.CreateDirectory(Path.Combine(workspace.FullName, ".wfx"));
        Directory.CreateDirectory(Path.Combine(userProfile, ".wfx"));
        File.WriteAllText(
            Path.Combine(workspace.FullName, ".wfx", "config.json"),
            """{ "provider": "local", "base_url": "https://example.test/v1", "model": "fake-model", "approval": "yolo" }""");
        File.WriteAllText(
            Path.Combine(userProfile, ".wfx", "config.json"),
            $$"""{ "mcp_servers": { "remote": { "url": "{{new Uri(mcpServer.BaseUri, "/mcp")}}" } } }""");
        Environment.CurrentDirectory = workspace.FullName;
        try
        {
            var exitCode = await CliRunner.RunAsync(
                ["run", "--json", "--quiet", "inspect"],
                CliRunner.CreateUnexpectedHttpClient("The injected provider must be used for the model."),
                new SessionStore(Path.Combine(workspace.FullName, "sessions")),
                TestContext.Current.CancellationToken,
                userProfile,
                modelProviderFactory: (_, _) => new ScriptedProvider([
                    new ModelCompleted(new ModelMessage(
                        ModelRole.Assistant,
                        null,
                        [new ModelToolCall("call-1", "mcp_remote_echo", "{\"text\":\"hi\"}")])),
                    new ModelCompleted(new ModelMessage(ModelRole.Assistant, "done"))
                ]));

            Assert.Equal(0, exitCode);
            Assert.Empty(console.ErrorText);
            var events = ParseLines(console.Output.ToString());
            Assert.Equal("turn_started", events[0].GetProperty("event").GetString());
            var started = Assert.Single(events, e => e.GetProperty("event").GetString() == "tool_started");
            Assert.Equal("mcp_remote_echo", started.GetProperty("name").GetString());
            Assert.Equal("{\"text\":\"hi\"}", started.GetProperty("arguments_json").GetString());
            var completed = Assert.Single(events, e => e.GetProperty("event").GetString() == "tool_completed");
            Assert.Equal("mcp_remote_echo", completed.GetProperty("name").GetString());
            Assert.Equal("completed", completed.GetProperty("outcome").GetString());
            Assert.Equal("turn_completed", events[^1].GetProperty("event").GetString());

            // The model saw the MCP tool in its catalog on the first request.
            Assert.Contains(mcpServer.Requests, request => request.Body.Contains("tools/call", StringComparison.Ordinal));
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
        }
    }

    [Fact]
    public async Task JsonRun_UnauthorizedHttpMcpServer_RemediatesSignIn()
    {
        var workspace = Directory.CreateTempSubdirectory("wfx-cli-tests-");
        using var console = new ConsoleCapture();
        var originalDirectory = Environment.CurrentDirectory;
        var userProfile = Path.Combine(workspace.FullName, "profile");
        using var mcpServer = new LoopbackHttpServer(_ => LoopbackResponse.Json("{}", status: 401));
        Directory.CreateDirectory(Path.Combine(workspace.FullName, ".wfx"));
        Directory.CreateDirectory(Path.Combine(userProfile, ".wfx"));
        File.WriteAllText(
            Path.Combine(workspace.FullName, ".wfx", "config.json"),
            """{ "provider": "local", "base_url": "https://example.test/v1", "model": "fake-model", "approval": "yolo" }""");
        File.WriteAllText(
            Path.Combine(userProfile, ".wfx", "config.json"),
            $$"""{ "mcp_servers": { "remote": { "url": "{{new Uri(mcpServer.BaseUri, "/mcp")}}" } } }""");
        Environment.CurrentDirectory = workspace.FullName;
        try
        {
            // --quiet suppresses chatter, never the remediation: it reaches stderr as one
            // structured JSON object. No browser is launched; the run completes unattended.
            var exitCode = await CliRunner.RunAsync(
                ["run", "--json", "--quiet", "inspect"],
                CliRunner.CreateUnexpectedHttpClient("The injected provider must be used for the model."),
                new SessionStore(Path.Combine(workspace.FullName, "sessions")),
                TestContext.Current.CancellationToken,
                userProfile,
                modelProviderFactory: (_, _) => new ScriptedProvider([
                    new ModelCompleted(new ModelMessage(ModelRole.Assistant, "done"))
                ]));

            Assert.Equal(0, exitCode);
            var warning = Assert.Single(ParseLines(console.ErrorText));
            Assert.Equal("warning", warning.GetProperty("event").GetString());
            Assert.Equal("mcp_authorization_required", warning.GetProperty("kind").GetString());
            Assert.Equal("remote", warning.GetProperty("server").GetString());
            Assert.Equal("wfx mcp auth remote", warning.GetProperty("remediation").GetString());
            Assert.Contains("wfx mcp auth remote", warning.GetProperty("message").GetString());
            var events = ParseLines(console.Output.ToString());
            Assert.Equal("turn_started", events[0].GetProperty("event").GetString());
            Assert.Equal("turn_completed", events[^1].GetProperty("event").GetString());
            Assert.DoesNotContain(events, e => e.GetProperty("event").GetString() == "tool_started");
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
        }
    }

    [Fact]
    public async Task JsonRun_WithStoredToken_ConnectsWithoutPrompting()
    {
        var workspace = Directory.CreateTempSubdirectory("wfx-cli-tests-");
        using var console = new ConsoleCapture();
        var originalDirectory = Environment.CurrentDirectory;
        var userProfile = Path.Combine(workspace.FullName, "profile");
        using var mcpServer = new LoopbackHttpServer(request =>
        {
            // The endpoint demands the stored bearer token on every call.
            if (!request.Headers.TryGetValue("Authorization", out var authorization) ||
                authorization != "Bearer stored-token")
            {
                return LoopbackResponse.Json("{\"error\":\"invalid_token\"}", status: 401);
            }

            using var document = JsonDocument.Parse(request.Body);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var id))
            {
                return LoopbackResponse.Accepted();
            }

            var result = root.GetProperty("method").GetString() switch
            {
                "initialize" => "{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"serverInfo\":{\"name\":\"fake\",\"version\":\"1.0\"}}",
                "tools/list" => "{\"tools\":[{\"name\":\"echo\",\"inputSchema\":{\"type\":\"object\"}}]}",
                _ => "{\"content\":[{\"type\":\"text\",\"text\":\"authorized-pong\"}]}"
            };
            return LoopbackResponse.Json(
                $"{{\"jsonrpc\":\"2.0\",\"id\":{id.GetRawText()},\"result\":{result}}}");
        });
        Directory.CreateDirectory(Path.Combine(workspace.FullName, ".wfx"));
        Directory.CreateDirectory(Path.Combine(userProfile, ".wfx"));
        File.WriteAllText(
            Path.Combine(workspace.FullName, ".wfx", "config.json"),
            """{ "provider": "local", "base_url": "https://example.test/v1", "model": "fake-model", "approval": "yolo" }""");
        File.WriteAllText(
            Path.Combine(userProfile, ".wfx", "config.json"),
            $$"""{ "mcp_servers": { "remote": { "url": "{{new Uri(mcpServer.BaseUri, "/mcp")}}" } } }""");
        File.WriteAllText(
            Path.Combine(userProfile, ".wfx", "mcp-tokens.json"),
            $$"""{"servers":{"remote":{"server_url":"{{new Uri(mcpServer.BaseUri, "/mcp")}}","access_token":"stored-token","refresh_token":"refresh-1","expires_at_utc":"{{DateTimeOffset.UtcNow.AddHours(1):O}}","token_endpoint":"{{new Uri(mcpServer.BaseUri, "/token")}}","client_id":"wfx"} } }""");
        Environment.CurrentDirectory = workspace.FullName;
        try
        {
            var exitCode = await CliRunner.RunAsync(
                ["run", "--json", "--quiet", "inspect"],
                CliRunner.CreateUnexpectedHttpClient("The injected provider must be used for the model."),
                new SessionStore(Path.Combine(workspace.FullName, "sessions")),
                TestContext.Current.CancellationToken,
                userProfile,
                modelProviderFactory: (_, _) => new ScriptedProvider([
                    new ModelCompleted(new ModelMessage(
                        ModelRole.Assistant,
                        null,
                        [new ModelToolCall("call-1", "mcp_remote_echo", "{}")])),
                    new ModelCompleted(new ModelMessage(ModelRole.Assistant, "done"))
                ]));

            Assert.Equal(0, exitCode);
            Assert.Empty(console.ErrorText);
            var events = ParseLines(console.Output.ToString());
            var completed = Assert.Single(events, e => e.GetProperty("event").GetString() == "tool_completed");
            Assert.Equal("mcp_remote_echo", completed.GetProperty("name").GetString());
            Assert.Equal("completed", completed.GetProperty("outcome").GetString());
            Assert.Contains("authorized-pong", completed.GetProperty("result").GetProperty("content").GetString());
            // The token never appears in the event stream or on stderr.
            Assert.DoesNotContain("stored-token", console.Output.ToString());
            Assert.DoesNotContain("stored-token", console.ErrorText);
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
        }
    }

    [Fact]
    public async Task JsonRun_UnauthorizedWithLiveRefreshToken_RefreshesAndConnects()
    {
        var workspace = Directory.CreateTempSubdirectory("wfx-cli-tests-");
        using var console = new ConsoleCapture();
        var originalDirectory = Environment.CurrentDirectory;
        var userProfile = Path.Combine(workspace.FullName, "profile");
        using var mcpServer = new LoopbackHttpServer(request =>
        {
            if (request.Path == "/token")
            {
                Assert.Contains("refresh_token=refresh-1", request.Body);
                return LoopbackResponse.Json(
                    """{"access_token":"fresh-token","refresh_token":"refresh-2","expires_in":3600,"token_type":"Bearer"}""");
            }

            // The stale access token is rejected with 401; the refreshed token connects.
            var authorized = request.Headers.TryGetValue("Authorization", out var authorization) &&
                authorization == "Bearer fresh-token";
            if (!authorized)
            {
                return LoopbackResponse.Json("{\"error\":\"invalid_token\"}", status: 401);
            }

            using var document = JsonDocument.Parse(request.Body);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var id))
            {
                return LoopbackResponse.Accepted();
            }

            var result = root.GetProperty("method").GetString() switch
            {
                "initialize" => "{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"serverInfo\":{\"name\":\"fake\",\"version\":\"1.0\"}}",
                "tools/list" => "{\"tools\":[{\"name\":\"echo\",\"inputSchema\":{\"type\":\"object\"}}]}",
                _ => "{\"content\":[{\"type\":\"text\",\"text\":\"refreshed-pong\"}]}"
            };
            return LoopbackResponse.Json(
                $"{{\"jsonrpc\":\"2.0\",\"id\":{id.GetRawText()},\"result\":{result}}}");
        });
        Directory.CreateDirectory(Path.Combine(workspace.FullName, ".wfx"));
        Directory.CreateDirectory(Path.Combine(userProfile, ".wfx"));
        File.WriteAllText(
            Path.Combine(workspace.FullName, ".wfx", "config.json"),
            """{ "provider": "local", "base_url": "https://example.test/v1", "model": "fake-model", "approval": "yolo" }""");
        File.WriteAllText(
            Path.Combine(userProfile, ".wfx", "config.json"),
            $$"""{ "mcp_servers": { "remote": { "url": "{{new Uri(mcpServer.BaseUri, "/mcp")}}" } } }""");
        File.WriteAllText(
            Path.Combine(userProfile, ".wfx", "mcp-tokens.json"),
            $$"""{"servers":{"remote":{"server_url":"{{new Uri(mcpServer.BaseUri, "/mcp")}}","access_token":"stale-token","refresh_token":"refresh-1","expires_at_utc":"{{DateTimeOffset.UtcNow.AddHours(1):O}}","token_endpoint":"{{new Uri(mcpServer.BaseUri, "/token")}}","client_id":"wfx"} } }""");
        Environment.CurrentDirectory = workspace.FullName;
        try
        {
            var exitCode = await CliRunner.RunAsync(
                ["run", "--json", "--quiet", "inspect"],
                CliRunner.CreateUnexpectedHttpClient("The injected provider must be used for the model."),
                new SessionStore(Path.Combine(workspace.FullName, "sessions")),
                TestContext.Current.CancellationToken,
                userProfile,
                modelProviderFactory: (_, _) => new ScriptedProvider([
                    new ModelCompleted(new ModelMessage(
                        ModelRole.Assistant,
                        null,
                        [new ModelToolCall("call-1", "mcp_remote_echo", "{}")])),
                    new ModelCompleted(new ModelMessage(ModelRole.Assistant, "done"))
                ]));

            Assert.Equal(0, exitCode);
            Assert.Empty(console.ErrorText);
            var events = ParseLines(console.Output.ToString());
            var completed = Assert.Single(events, e => e.GetProperty("event").GetString() == "tool_completed");
            Assert.Equal("completed", completed.GetProperty("outcome").GetString());
            Assert.Contains("refreshed-pong", completed.GetProperty("result").GetProperty("content").GetString());
            var storeText = File.ReadAllText(Path.Combine(userProfile, ".wfx", "mcp-tokens.json"));
            Assert.Contains("fresh-token", storeText);
            // Neither the stale nor the refreshed token ever appears in the output.
            Assert.DoesNotContain("stale-token", console.Output.ToString());
            Assert.DoesNotContain("fresh-token", console.Output.ToString());
            Assert.DoesNotContain("fresh-token", console.ErrorText);
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
        }
    }

    [Fact]
    public async Task InteractiveRun_ApprovalPrompt_ShowsMcpToolNameAndExactArguments()
    {
        var workspace = Directory.CreateTempSubdirectory("wfx-cli-tests-");
        // The human answers "y" at the prompt.
        using var console = new ConsoleCapture("y\n");
        var originalDirectory = Environment.CurrentDirectory;
        var userProfile = Path.Combine(workspace.FullName, "profile");
        using var mcpServer = new LoopbackHttpServer(request =>
        {
            using var document = JsonDocument.Parse(request.Body);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var id))
            {
                return LoopbackResponse.Accepted();
            }

            var result = root.GetProperty("method").GetString() switch
            {
                "initialize" => "{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"serverInfo\":{\"name\":\"fake\",\"version\":\"1.0\"}}",
                "tools/list" => "{\"tools\":[{\"name\":\"echo\",\"inputSchema\":{\"type\":\"object\"}}]}",
                _ => "{\"content\":[{\"type\":\"text\",\"text\":\"approved-pong\"}]}"
            };
            return LoopbackResponse.Json(
                $"{{\"jsonrpc\":\"2.0\",\"id\":{id.GetRawText()},\"result\":{result}}}");
        });
        Directory.CreateDirectory(Path.Combine(workspace.FullName, ".wfx"));
        Directory.CreateDirectory(Path.Combine(userProfile, ".wfx"));
        File.WriteAllText(
            Path.Combine(workspace.FullName, ".wfx", "config.json"),
            """{ "provider": "local", "base_url": "https://example.test/v1", "model": "fake-model", "approval": "always" }""");
        File.WriteAllText(
            Path.Combine(userProfile, ".wfx", "config.json"),
            $$"""{ "mcp_servers": { "remote": { "url": "{{new Uri(mcpServer.BaseUri, "/mcp")}}" } } }""");
        Environment.CurrentDirectory = workspace.FullName;
        try
        {
            var exitCode = await CliRunner.RunAsync(
                ["run", "inspect"],
                CliRunner.CreateUnexpectedHttpClient("The injected provider must be used for the model."),
                new SessionStore(Path.Combine(workspace.FullName, "sessions")),
                TestContext.Current.CancellationToken,
                userProfile,
                modelProviderFactory: (_, _) => new ScriptedProvider([
                    new ModelCompleted(new ModelMessage(
                        ModelRole.Assistant,
                        null,
                        [new ModelToolCall("call-1", "mcp_remote_echo", "{\"text\":\"hi\"}")])),
                    new ModelCompleted(new ModelMessage(ModelRole.Assistant, "done"))
                ]));

            Assert.Equal(0, exitCode);
            // The prompt names the tool and shows the exact arguments before the human answers.
            Assert.Contains("Approve mcp_remote_echo(", console.ErrorText);
            Assert.Contains("text: hi", console.ErrorText);
            Assert.Contains("SystemChange", console.ErrorText);
            // The approval was real: the call went out only after the "y".
            Assert.Contains(mcpServer.Requests, request => request.Body.Contains("tools/call", StringComparison.Ordinal));
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
        }
    }

    [Fact]
    public async Task JsonRun_MidCallUnauthorized_FailsTheToolWithRemediation()
    {
        var workspace = Directory.CreateTempSubdirectory("wfx-cli-tests-");
        using var console = new ConsoleCapture();
        var originalDirectory = Environment.CurrentDirectory;
        var userProfile = Path.Combine(workspace.FullName, "profile");
        // The handshake and tools/list succeed; only the call is refused.
        using var mcpServer = new LoopbackHttpServer(request =>
        {
            using var document = JsonDocument.Parse(request.Body);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var id))
            {
                return LoopbackResponse.Accepted();
            }

            if (root.GetProperty("method").GetString() == "tools/call")
            {
                return LoopbackResponse.Json("{\"error\":\"invalid_token\"}", status: 401);
            }

            var result = root.GetProperty("method").GetString() == "initialize"
                ? "{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"serverInfo\":{\"name\":\"fake\",\"version\":\"1.0\"}}"
                : "{\"tools\":[{\"name\":\"echo\",\"inputSchema\":{\"type\":\"object\"}}]}";
            return LoopbackResponse.Json(
                $"{{\"jsonrpc\":\"2.0\",\"id\":{id.GetRawText()},\"result\":{result}}}");
        });
        Directory.CreateDirectory(Path.Combine(workspace.FullName, ".wfx"));
        Directory.CreateDirectory(Path.Combine(userProfile, ".wfx"));
        File.WriteAllText(
            Path.Combine(workspace.FullName, ".wfx", "config.json"),
            """{ "provider": "local", "base_url": "https://example.test/v1", "model": "fake-model", "approval": "yolo" }""");
        File.WriteAllText(
            Path.Combine(userProfile, ".wfx", "config.json"),
            $$"""{ "mcp_servers": { "remote": { "url": "{{new Uri(mcpServer.BaseUri, "/mcp")}}" } } }""");
        Environment.CurrentDirectory = workspace.FullName;
        try
        {
            var exitCode = await CliRunner.RunAsync(
                ["run", "--json", "--quiet", "inspect"],
                CliRunner.CreateUnexpectedHttpClient("The injected provider must be used for the model."),
                new SessionStore(Path.Combine(workspace.FullName, "sessions")),
                TestContext.Current.CancellationToken,
                userProfile,
                modelProviderFactory: (_, _) => new ScriptedProvider([
                    new ModelCompleted(new ModelMessage(
                        ModelRole.Assistant,
                        null,
                        [new ModelToolCall("call-1", "mcp_remote_echo", "{}")])),
                    new ModelCompleted(new ModelMessage(ModelRole.Assistant, "done"))
                ]));

            Assert.Equal(0, exitCode);
            var events = ParseLines(console.Output.ToString());
            var completed = Assert.Single(events, e => e.GetProperty("event").GetString() == "tool_completed");
            Assert.Equal("mcp_remote_echo", completed.GetProperty("name").GetString());
            Assert.Equal("failed", completed.GetProperty("outcome").GetString());
            Assert.Contains(
                "wfx mcp auth remote",
                completed.GetProperty("result").GetProperty("content").GetString());
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
        }
    }

    [Fact]
    public async Task JsonRun_ToolResultEchoingStoredToken_IsRedactedEverywhere()
    {
        const string token = "opaque-stored-token-xyz";
        var workspace = Directory.CreateTempSubdirectory("wfx-cli-tests-");
        using var console = new ConsoleCapture();
        var originalDirectory = Environment.CurrentDirectory;
        var userProfile = Path.Combine(workspace.FullName, "profile");
        // The server answers only to the stored bearer token and then echoes the raw token
        // back in the tool result — without the "Bearer " prefix, so shape-based redaction
        // cannot catch it; only the stored-token set can.
        using var mcpServer = new LoopbackHttpServer(request =>
        {
            if (!request.Headers.TryGetValue("Authorization", out var authorization) ||
                authorization != $"Bearer {token}")
            {
                return LoopbackResponse.Json("{\"error\":\"invalid_token\"}", status: 401);
            }

            using var document = JsonDocument.Parse(request.Body);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var id))
            {
                return LoopbackResponse.Accepted();
            }

            var result = root.GetProperty("method").GetString() switch
            {
                "initialize" => "{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"serverInfo\":{\"name\":\"fake\",\"version\":\"1.0\"}}",
                "tools/list" => "{\"tools\":[{\"name\":\"echo\",\"inputSchema\":{\"type\":\"object\"}}]}",
                _ => $"{{\"content\":[{{\"type\":\"text\",\"text\":\"leaked:{token}\"}}]}}"
            };
            return LoopbackResponse.Json(
                $"{{\"jsonrpc\":\"2.0\",\"id\":{id.GetRawText()},\"result\":{result}}}");
        });
        var sessions = Path.Combine(workspace.FullName, "sessions");
        Directory.CreateDirectory(Path.Combine(workspace.FullName, ".wfx"));
        Directory.CreateDirectory(Path.Combine(userProfile, ".wfx"));
        File.WriteAllText(
            Path.Combine(workspace.FullName, ".wfx", "config.json"),
            """{ "provider": "local", "base_url": "https://example.test/v1", "model": "fake-model", "approval": "yolo" }""");
        File.WriteAllText(
            Path.Combine(userProfile, ".wfx", "config.json"),
            $$"""{ "mcp_servers": { "remote": { "url": "{{new Uri(mcpServer.BaseUri, "/mcp")}}" } } }""");
        File.WriteAllText(
            Path.Combine(userProfile, ".wfx", "mcp-tokens.json"),
            $$"""{"servers":{"remote":{"server_url":"{{new Uri(mcpServer.BaseUri, "/mcp")}}","access_token":"{{token}}","expires_at_utc":"{{DateTimeOffset.UtcNow.AddHours(1):O}}","token_endpoint":"{{new Uri(mcpServer.BaseUri, "/token")}}","client_id":"wfx"} } }""");
        Environment.CurrentDirectory = workspace.FullName;
        try
        {
            // The model also echoes the token in the tool-call arguments.
            var exitCode = await CliRunner.RunAsync(
                ["run", "--json", "--quiet", "inspect"],
                CliRunner.CreateUnexpectedHttpClient("The injected provider must be used for the model."),
                new SessionStore(sessions),
                TestContext.Current.CancellationToken,
                userProfile,
                modelProviderFactory: (_, _) => new ScriptedProvider([
                    new ModelCompleted(new ModelMessage(
                        ModelRole.Assistant,
                        null,
                        [new ModelToolCall("call-1", "mcp_remote_echo", $"{{\"text\":\"{token}\"}}")])),
                    new ModelCompleted(new ModelMessage(ModelRole.Assistant, "done"))
                ]));

            Assert.Equal(0, exitCode);
            Assert.DoesNotContain(token, console.Output.ToString());
            Assert.DoesNotContain(token, console.ErrorText);
            var events = ParseLines(console.Output.ToString());
            var completed = Assert.Single(events, e => e.GetProperty("event").GetString() == "tool_completed");
            Assert.Equal("completed", completed.GetProperty("outcome").GetString());
            Assert.Contains("[REDACTED]", completed.GetProperty("result").GetProperty("content").GetString());
            var started = Assert.Single(events, e => e.GetProperty("event").GetString() == "tool_started");
            Assert.Contains("[REDACTED]", started.GetProperty("arguments_json").GetString());
            // The persisted transcript is equally clean.
            Assert.All(Directory.GetFiles(sessions, "*", SearchOption.AllDirectories),
                file => Assert.DoesNotContain(token, File.ReadAllText(file)));
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
        }
    }

    private static JsonElement[] ParseLines(string output) => output
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(static line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        })
        .ToArray();

    private sealed class ScriptedProvider(IReadOnlyList<ModelCompleted> responses) : IModelProvider
    {
        private int _index;

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return responses[_index++];
        }
    }
}
