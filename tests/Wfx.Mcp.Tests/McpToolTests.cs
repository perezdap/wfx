using System.Text.Json;
using System.Text.Json.Nodes;
using Wfx.Core;
using Wfx.Mcp;

namespace Wfx.Mcp.Tests;

public sealed class McpToolTests
{
    [Fact]
    public void Classify_IsAlwaysSystemChange()
    {
        var tool = CreateTool();

        using var empty = JsonDocument.Parse("{}");
        using var scary = JsonDocument.Parse("""{"path":"C:\\Windows\\System32","recursive":true}""");

        Assert.Equal(ApprovalLevel.SystemChange, tool.Classify(empty.RootElement));
        Assert.Equal(ApprovalLevel.SystemChange, tool.Classify(scary.RootElement));
    }

    [Fact]
    public void Definition_PassesThroughServerSchema()
    {
        var schema = JsonNode.Parse("""
            {"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}
            """);
        var tool = new McpTool(
            "fake",
            new McpToolInfo("echo", "Echoes input.", schema),
            CreateClient(),
            "mcp_fake_echo");

        Assert.Equal("mcp_fake_echo", tool.Definition.Name);
        Assert.Equal("Echoes input.", tool.Definition.Description);
        Assert.Equal("object", tool.Definition.Parameters["type"]!.GetValue<string>());
        Assert.NotNull(tool.Definition.Parameters["properties"]);
    }

    [Fact]
    public void Definition_DefaultsToObjectSchema_WhenServerGivesNone()
    {
        var tool = CreateTool();

        Assert.Equal("object", tool.Definition.Parameters["type"]!.GetValue<string>());
        Assert.StartsWith("MCP tool", tool.Definition.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Execute_MapsServerErrorFlag_ToToolFailure()
    {
        using var pipes = new TestPipes();
        var server = Task.Run(() => FakeMcpServer.RunAsync(pipes.ServerReader, pipes.ServerWriter, (method, request) =>
            method == "tools/call"
                ? FakeMcpServer.Response(request, """
                    {"isError":true,"content":[{"type":"text","text":"tool exploded"}]}
                    """)
                : null), TestContext.Current.CancellationToken);

        var client = CreateClient(pipes);
        var tool = new McpTool("fake", new McpToolInfo("echo", null, null), client, "mcp_fake_echo");
        using var arguments = JsonDocument.Parse("{}");

        var result = await tool.ExecuteAsync(
            arguments.RootElement,
            new ToolContext(Directory.GetCurrentDirectory()),
            TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("tool exploded", result.Error);

        await client.DisposeAsync();
        pipes.Dispose();
        await server.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Execute_RpcError_FailsStructurally()
    {
        using var pipes = new TestPipes();
        var server = Task.Run(() => FakeMcpServer.RunAsync(pipes.ServerReader, pipes.ServerWriter, (method, request) =>
            method == "tools/call"
                ? FakeMcpServer.Error(request, -32602, "Invalid params")
                : null), TestContext.Current.CancellationToken);

        var client = CreateClient(pipes);
        var tool = new McpTool("fake", new McpToolInfo("echo", null, null), client, "mcp_fake_echo");
        using var arguments = JsonDocument.Parse("{}");

        var result = await tool.ExecuteAsync(
            arguments.RootElement,
            new ToolContext(Directory.GetCurrentDirectory()),
            TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("-32602", result.Error);
        Assert.Contains("Invalid params", result.Error);

        await client.DisposeAsync();
        pipes.Dispose();
        await server.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Execute_ServerExitMidCall_FailsStructurally()
    {
        using var pipes = new TestPipes();
        // The server reads the tools/call request and then dies without answering.
        var server = Task.Run(async () =>
        {
            await pipes.ServerReader.ReadLineAsync(TestContext.Current.CancellationToken);
            pipes.CloseServerSide();
        }, TestContext.Current.CancellationToken);

        var client = CreateClient(pipes);
        var tool = new McpTool("fake", new McpToolInfo("echo", null, null), client, "mcp_fake_echo");
        using var arguments = JsonDocument.Parse("{}");

        var result = await tool.ExecuteAsync(
            arguments.RootElement,
            new ToolContext(Directory.GetCurrentDirectory()),
            TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("exited", result.Error);

        pipes.Dispose();
        await server.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }

    private static McpTool CreateTool() =>
        new("fake", new McpToolInfo("echo", null, null), CreateClient(), "mcp_fake_echo");

    private static McpStdioClient CreateClient()
    {
        // The session never runs: these calls only exercise definition and classification.
        var session = new McpJsonRpcSession(TextWriter.Null, new StringReader(string.Empty));
        return new McpStdioClient(session, new RecordingDisposable());
    }

    private static McpStdioClient CreateClient(TestPipes pipes)
    {
        var session = new McpJsonRpcSession(pipes.ClientWriter, pipes.ClientReader);
        var client = new McpStdioClient(session, new RecordingDisposable());
        session.StartReadLoop();
        return client;
    }
}
