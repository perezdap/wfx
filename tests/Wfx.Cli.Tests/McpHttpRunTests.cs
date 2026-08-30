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
            // No --quiet: the remediation warning must reach stderr. No browser is launched:
            // the run completes without prompting.
            var exitCode = await CliRunner.RunAsync(
                ["run", "--json", "inspect"],
                CliRunner.CreateUnexpectedHttpClient("The injected provider must be used for the model."),
                new SessionStore(Path.Combine(workspace.FullName, "sessions")),
                TestContext.Current.CancellationToken,
                userProfile,
                modelProviderFactory: (_, _) => new ScriptedProvider([
                    new ModelCompleted(new ModelMessage(ModelRole.Assistant, "done"))
                ]));

            Assert.Equal(0, exitCode);
            Assert.Contains("wfx mcp auth remote", console.ErrorText);
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
