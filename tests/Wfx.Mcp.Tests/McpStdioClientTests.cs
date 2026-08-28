using System.Collections.Concurrent;
using System.Text.Json;
using Wfx.Core;
using Wfx.Mcp;

namespace Wfx.Mcp.Tests;

public sealed class McpStdioClientTests
{
    [Fact]
    public async Task HandshakeAndToolListing_RoundTrip()
    {
        using var pipes = new TestPipes();
        var seenMethods = new ConcurrentQueue<string>();
        string? initializeParams = null;
        var server = Task.Run(() => FakeMcpServer.RunAsync(pipes.ServerReader, pipes.ServerWriter, (method, request) =>
        {
            seenMethods.Enqueue(method);
            switch (method)
            {
                case "initialize":
                    initializeParams = request.GetProperty("params").GetRawText();
                    return FakeMcpServer.Response(request, """
                        {"protocolVersion":"2025-06-18","capabilities":{},"serverInfo":{"name":"fake","version":"1.0"}}
                        """);
                case "tools/list":
                    return FakeMcpServer.Response(request, """{"tools":[{"name":"echo","description":"Echoes input.","inputSchema":{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}},{"name":"no-schema"}]}""");
                default:
                    return null;
            }
        }), TestContext.Current.CancellationToken);

        await using var owner = new RecordingDisposable();
        var client = CreateClient(pipes, owner);
        var cancellationToken = TestContext.Current.CancellationToken;

        await client.InitializeAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var tools = await client.ListToolsAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(2, tools.Count);
        Assert.Equal("echo", tools[0].Name);
        Assert.Equal("Echoes input.", tools[0].Description);
        Assert.NotNull(tools[0].InputSchema);
        Assert.Equal("no-schema", tools[1].Name);
        Assert.Null(tools[1].InputSchema);

        Assert.Contains("initialize", seenMethods);
        Assert.Contains("notifications/initialized", seenMethods);
        Assert.Contains("tools/list", seenMethods);
        Assert.NotNull(initializeParams);
        Assert.Contains(McpStdioClient.OfferedProtocolVersion, initializeParams);
        Assert.Contains("\"wfx\"", initializeParams);

        await client.DisposeAsync();
        pipes.Dispose();
        await server.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CallTool_RoundTripsTextContent()
    {
        using var pipes = new TestPipes();
        string? callParams = null;
        var server = Task.Run(() => FakeMcpServer.RunAsync(pipes.ServerReader, pipes.ServerWriter, (method, request) =>
        {
            if (method != "tools/call")
            {
                return null;
            }

            callParams = request.GetProperty("params").GetRawText();
            return FakeMcpServer.Response(request, """
                {"content":[{"type":"text","text":"echo:hello"},{"type":"image","data":"ignored"},{"type":"text","text":"second"}]}
                """);
        }), TestContext.Current.CancellationToken);

        await using var owner = new RecordingDisposable();
        var client = CreateClient(pipes, owner);
        using var arguments = JsonDocument.Parse("""{"text":"hello"}""");

        var result = await client.CallToolAsync("echo", arguments.RootElement, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        Assert.Equal($"echo:hello{Environment.NewLine}second", result.Output);
        Assert.NotNull(callParams);
        Assert.Contains("\"name\":\"echo\"", callParams);
        Assert.Contains("\"text\":\"hello\"", callParams);

        await client.DisposeAsync();
        pipes.Dispose();
        await server.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task MalformedLine_BeforeValidResponse_DoesNotBreakSession()
    {
        using var pipes = new TestPipes();
        var server = Task.Run(() => FakeMcpServer.RunAsync(pipes.ServerReader, pipes.ServerWriter, (method, request) =>
        {
            if (method != "tools/call")
            {
                return null;
            }

            // One garbage frame, then the real response; the client must skip the garbage.
            return "this is not json-rpc\n" + FakeMcpServer.Response(request, """
                {"content":[{"type":"text","text":"ok"}]}
                """);
        }), TestContext.Current.CancellationToken);

        await using var owner = new RecordingDisposable();
        var client = CreateClient(pipes, owner);
        using var arguments = JsonDocument.Parse("{}");

        var result = await client.CallToolAsync("echo", arguments.RootElement, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        Assert.Equal("ok", result.Output);

        await client.DisposeAsync();
        pipes.Dispose();
        await server.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Cancellation_FailsTheCallAndDisposesTheProcess()
    {
        using var pipes = new TestPipes();
        // The fake server reads the request but never answers, so the call stays pending.
        var server = Task.Run(() => FakeMcpServer.RunAsync(pipes.ServerReader, pipes.ServerWriter, (_, _) => null), TestContext.Current.CancellationToken);

        var owner = new RecordingDisposable();
        var client = CreateClient(pipes, owner);
        var tool = new McpTool("fake", new McpToolInfo("echo", null, null), client, "mcp_fake_echo");
        using var arguments = JsonDocument.Parse("{}");
        using var cancellation = new CancellationTokenSource();
        var call = tool.ExecuteAsync(arguments.RootElement, new ToolContext(Directory.GetCurrentDirectory()), cancellation.Token);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.AsTask());
        Assert.Equal(1, owner.DisposeCount);

        // The server is dead for the rest of the run: the next call fails structurally.
        var next = await tool.ExecuteAsync(
            arguments.RootElement,
            new ToolContext(Directory.GetCurrentDirectory()),
            TestContext.Current.CancellationToken);
        Assert.False(next.Success);
        Assert.NotNull(next.Error);

        pipes.Dispose();
        await server.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Cancellation_KillsARealServerProcess()
    {
        using var workspace = new TemporaryDirectory();
        var settings = new McpServerSettings(
            "cmd.exe",
            ["/c", "ping", "-n", "120", "127.0.0.1"],
            new Dictionary<string, string>());
        var client = await McpStdioClient.StartAsync(settings, workspace.Path, TestContext.Current.CancellationToken);
        var owner = client.Owner;
        Assert.NotNull(owner);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        using var arguments = JsonDocument.Parse("{}");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.CallToolAsync("echo", arguments.RootElement, cancellation.Token));
        await client.DisposeAsync();

        // Kill propagates to the whole process tree and disposal waits for exit.
        await owner!.Exited.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
    }

    private static McpStdioClient CreateClient(TestPipes pipes, RecordingDisposable owner)
    {
        var session = new McpJsonRpcSession(pipes.ClientWriter, pipes.ClientReader);
        var client = new McpStdioClient(session, owner);
        session.StartReadLoop();
        return client;
    }
}
