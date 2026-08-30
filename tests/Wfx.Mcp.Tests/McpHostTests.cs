using System.Text.Json;
using Wfx.Core;
using Wfx.Mcp;

using Wfx.Testing;

namespace Wfx.Mcp.Tests;

public sealed class McpHostTests
{
    [Fact]
    public async Task ConnectAsync_UnavailableServer_WarnsAndContributesNoTools()
    {
        using var workspace = new TemporaryDirectory();
        var warnings = new List<string>();
        var servers = new Dictionary<string, McpServerSettings>
        {
            ["missing"] = McpServerSettings.ForStdio("no-such-command-wfx.exe", [], new Dictionary<string, string>())
        };

        await using var host = await McpHost.ConnectAsync(servers, workspace.Path, warnings.Add, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Empty(host.Tools);
        var warning = Assert.Single(warnings);
        Assert.Contains("'missing'", warning);
    }

    [Fact]
    public async Task Assemble_KeepsFirstTool_WhenSanitizedNamesCollide()
    {
        var warnings = new List<string>();

        await using var host = McpHost.Assemble(
        [
            new McpHost.ConnectedServer("alpha", CreateIdleClient(), [new McpToolInfo("echo", null, null)]),
            new McpHost.ConnectedServer("gam-ma", CreateIdleClient(), [new McpToolInfo("ping", null, null)]),
            // Sanitizes to the same name as 'gam-ma' and must lose.
            new McpHost.ConnectedServer("gam_ma", CreateIdleClient(), [new McpToolInfo("ping", null, null)]),
            // The same server listing the same tool twice.
            new McpHost.ConnectedServer(
                "delta",
                CreateIdleClient(),
                [new McpToolInfo("ping", null, null), new McpToolInfo("ping", null, null)])
        ],
            warnings.Add);

        Assert.Equal(3, host.Tools.Count);
        Assert.Equal("mcp_alpha_echo", host.Tools[0].Definition.Name);
        Assert.Equal("mcp_gam_ma_ping", host.Tools[1].Definition.Name);
        Assert.Equal("mcp_delta_ping", host.Tools[2].Definition.Name);
        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, warning => warning.Contains("'gam_ma'"));
        Assert.Contains(warnings, warning => warning.Contains("'delta'"));
    }

    [Fact]
    public async Task Assemble_CollisionFromSanitizationIsStillDetected()
    {
        var warnings = new List<string>();

        await using var host = McpHost.Assemble(
        [
            new McpHost.ConnectedServer("a-b", CreateIdleClient(), [new McpToolInfo("c", null, null)]),
            new McpHost.ConnectedServer("a_b", CreateIdleClient(), [new McpToolInfo("c", null, null)])
        ],
            warnings.Add);

        var tool = Assert.Single(host.Tools);
        Assert.Equal("mcp_a_b_c", tool.Definition.Name);
        Assert.Single(warnings);
    }

    [Fact]
    public async Task ConnectAsync_HttpServer_SurfacesNamespacedTools()
    {
        using var server = new LoopbackHttpServer(request =>
        {
            using var document = JsonDocument.Parse(request.Body);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var id))
            {
                return LoopbackResponse.Accepted();
            }

            var method = root.GetProperty("method").GetString();
            var response = method switch
            {
                "initialize" => "{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"serverInfo\":{\"name\":\"fake\",\"version\":\"1.0\"}}",
                "tools/list" => "{\"tools\":[{\"name\":\"echo\",\"description\":\"Echoes.\",\"inputSchema\":{\"type\":\"object\"}}]}",
                _ => "{\"content\":[{\"type\":\"text\",\"text\":\"pong\"}]}"
            };
            return LoopbackResponse.Json(
                $"{{\"jsonrpc\":\"2.0\",\"id\":{id.GetRawText()},\"result\":{response}}}");
        });
        using var workspace = new TemporaryDirectory();
        var warnings = new List<string>();
        var servers = new Dictionary<string, McpServerSettings>
        {
            ["remote"] = McpServerSettings.ForHttp(new Uri(server.BaseUri, "/mcp").ToString())
        };

        await using var host = await McpHost.ConnectAsync(
                servers, workspace.Path, warnings.Add, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Empty(warnings);
        var tool = Assert.Single(host.Tools);
        Assert.Equal("mcp_remote_echo", tool.Definition.Name);
        using var arguments = JsonDocument.Parse("{}");
        Assert.Equal(ApprovalLevel.SystemChange, tool.Classify(arguments.RootElement));

        var result = await tool.ExecuteAsync(
            arguments.RootElement,
            new ToolContext(workspace.Path),
            TestContext.Current.CancellationToken);
        Assert.True(result.Success);
        Assert.Equal("pong", result.Output);
    }

    [Fact]
    public async Task ConnectAsync_UnauthorizedServer_RecordsReminderInsteadOfWarning()
    {
        using var server = new LoopbackHttpServer(_ => LoopbackResponse.Json("{}", status: 401));
        using var workspace = new TemporaryDirectory();
        var warnings = new List<string>();
        var servers = new Dictionary<string, McpServerSettings>
        {
            ["remote"] = McpServerSettings.ForHttp(new Uri(server.BaseUri, "/mcp").ToString())
        };

        await using var host = await McpHost.ConnectAsync(
                servers, workspace.Path, warnings.Add, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Empty(host.Tools);
        Assert.Empty(warnings);
        var reminder = Assert.Single(host.AuthorizationReminders);
        Assert.Equal("remote", reminder.ServerName);
        Assert.Equal("wfx mcp auth remote", reminder.Command);
        Assert.Contains("wfx mcp auth remote", reminder.Message);
    }

    [Fact]
    public async Task ConnectAsync_SecretsContainHeaderValuesAndStoredTokens()
    {
        using var server = new LoopbackHttpServer(request =>
        {
            using var document = JsonDocument.Parse(request.Body);
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var id))
            {
                return LoopbackResponse.Accepted();
            }

            var result = root.GetProperty("method").GetString() == "initialize"
                ? "{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"serverInfo\":{\"name\":\"fake\",\"version\":\"1.0\"}}"
                : "{\"tools\":[]}";
            return LoopbackResponse.Json(
                $"{{\"jsonrpc\":\"2.0\",\"id\":{id.GetRawText()},\"result\":{result}}}");
        });
        using var workspace = new TemporaryDirectory();
        var store = new McpTokenStore(Path.Combine(workspace.Path, "mcp-tokens.json"));
        store.Save("remote", new McpTokenRecord(
            new Uri(server.BaseUri, "/mcp").ToString(), "stored-access", "stored-refresh",
            DateTimeOffset.UtcNow.AddHours(1), "https://auth.example.com/token", "wfx"));
        var servers = new Dictionary<string, McpServerSettings>
        {
            ["remote"] = McpServerSettings.ForHttp(
                new Uri(server.BaseUri, "/mcp").ToString(),
                new Dictionary<string, string> { ["X-Api-Key"] = "header-secret" })
        };

        await using var host = await McpHost.ConnectAsync(
                servers, workspace.Path, _ => { }, store, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Empty(host.Tools);
        Assert.Contains("header-secret", host.Secrets);
        Assert.Contains("stored-access", host.Secrets);
        Assert.Contains("stored-refresh", host.Secrets);
    }

    private static McpStdioClient CreateIdleClient()
    {
        var session = new McpJsonRpcSession(TextWriter.Null, new StringReader(string.Empty));
        return new McpStdioClient(session, new RecordingDisposable());
    }
}

