using System.Net;
using Wfx.Core;
using Wfx.Providers;

namespace Wfx.Providers.Tests;

public sealed class OpenAiCompatibleProviderTests
{
    [Fact]
    public async Task StreamsTextAndSerializesTools()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
            data: {"choices":[{"delta":{"content":"hello "}}]}

            data: {"choices":[{"delta":{"content":"world"}}],"usage":{"prompt_tokens":4,"completion_tokens":2}}

            data: [DONE]

            """, "text/event-stream");
        var provider = CreateProvider(handler);
        var request = new ModelRequest(
            "test-model",
            [new ModelMessage(ModelRole.User, "hi")],
            [new ToolDefinition("sample", "Sample tool.", new System.Text.Json.Nodes.JsonObject { ["type"] = "object" })]);

        var events = await CollectAsync(provider.StreamAsync(request, TestContext.Current.CancellationToken));

        Assert.Equal(["hello ", "world"], events.OfType<ModelTextDelta>().Select(static delta => delta.Text).ToArray());
        var completed = Assert.Single(events.OfType<ModelCompleted>());
        Assert.Equal("hello world", completed.Message.Content);
        Assert.Equal(4, completed.Usage!.InputTokens);
        Assert.Contains("\"stream\":true", handler.RequestBody);
        Assert.Contains("\"name\":\"sample\"", handler.RequestBody);
        Assert.DoesNotContain("stream_options", handler.RequestBody);
    }

    [Fact]
    public async Task IncludesStreamOptionsWhenRequested()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
            data: {"choices":[{"delta":{"content":"ok"}}]}

            data: [DONE]

            """, "text/event-stream");
        var provider = CreateProvider(handler, includeStreamOptions: true);

        await CollectAsync(provider.StreamAsync(
            new ModelRequest("test-model", [new ModelMessage(ModelRole.User, "hi")], []),
            TestContext.Current.CancellationToken));

        Assert.Contains("stream_options", handler.RequestBody);
        Assert.Contains("include_usage", handler.RequestBody);
    }

    [Fact]
    public async Task SkipsUsageOnlyTerminalChunkAndReportsUsage()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
            data: {"choices":[{"delta":{"content":"hello"}}]}

            data: {"usage":{"prompt_tokens":4,"completion_tokens":2}}

            data: [DONE]

            """, "text/event-stream");
        var provider = CreateProvider(handler, includeStreamOptions: true);

        var events = await CollectAsync(provider.StreamAsync(
            new ModelRequest("test-model", [new ModelMessage(ModelRole.User, "hi")], []),
            TestContext.Current.CancellationToken));

        var completed = Assert.Single(events.OfType<ModelCompleted>());
        Assert.Equal("hello", completed.Message.Content);
        Assert.Equal(4, completed.Usage!.InputTokens);
        Assert.Equal(2, completed.Usage!.OutputTokens);
    }

    [Fact]
    public async Task ReassemblesFragmentedToolCallArguments()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call-1","function":{"name":"read_file","arguments":"{\"pa"}}]}}]}

            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"th\":\"README.md\"}"}}]}}]}

            data: [DONE]

            """, "text/event-stream");
        var provider = CreateProvider(handler);

        var events = await CollectAsync(provider.StreamAsync(new ModelRequest(
            "test-model",
            [new ModelMessage(ModelRole.User, "inspect")],
            []), TestContext.Current.CancellationToken));

        var call = Assert.Single(Assert.Single(events.OfType<ModelCompleted>()).Message.ToolCalls!);
        Assert.Equal("call-1", call.Id);
        Assert.Equal("read_file", call.Name);
        Assert.Equal("{\"path\":\"README.md\"}", call.ArgumentsJson);
    }

    [Fact]
    public async Task RejectsMalformedStreamingJson()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "data: {not-json}\n\ndata: [DONE]\n", "text/event-stream");
        var provider = CreateProvider(handler);

        await Assert.ThrowsAsync<ProviderProtocolException>(async () =>
            await CollectAsync(provider.StreamAsync(
                new ModelRequest("model", [new ModelMessage(ModelRole.User, "x")], []),
                TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task RedactsApiKeyFromEndpointErrors()
    {
        const string secret = "top-secret-value";
        var handler = new StubHandler(HttpStatusCode.BadRequest, $"error echoed {secret}", "application/json");
        var provider = CreateProvider(handler, secret);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await CollectAsync(provider.StreamAsync(
                new ModelRequest("model", [new ModelMessage(ModelRole.User, "x")], []),
                TestContext.Current.CancellationToken)));

        Assert.DoesNotContain(secret, exception.Message);
        Assert.Contains("[REDACTED]", exception.Message);
    }

    [Fact]
    public async Task RedactsCustomHeaderValuesFromEndpointErrors()
    {
        const string secret = "header-secret-value";
        var handler = new StubHandler(HttpStatusCode.Unauthorized, $"credential {secret}", "application/json");
        var provider = CreateProvider(handler, headers: new Dictionary<string, string>
        {
            ["x-api-key"] = secret
        });

        var exception = await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await CollectAsync(provider.StreamAsync(
                new ModelRequest("model", [new ModelMessage(ModelRole.User, "x")], []),
                TestContext.Current.CancellationToken)));

        Assert.DoesNotContain(secret, exception.Message);
        Assert.Contains("[REDACTED]", exception.Message);
    }

    private static OpenAiCompatibleProvider CreateProvider(
        StubHandler handler,
        string? apiKey = null,
        bool includeStreamOptions = false,
        IReadOnlyDictionary<string, string>? headers = null) =>
        new(new HttpClient(handler), new OpenAiProviderOptions
        {
            BaseUri = new Uri("https://example.test/v1"),
            ApiKey = apiKey,
            Headers = headers ?? new Dictionary<string, string>(),
            Timeout = TimeSpan.FromSeconds(10),
            IncludeStreamOptions = includeStreamOptions
        });

    private static async Task<List<ModelStreamEvent>> CollectAsync(IAsyncEnumerable<ModelStreamEvent> source)
    {
        var result = new List<ModelStreamEvent>();
        await foreach (var item in source)
        {
            result.Add(item);
        }

        return result;
    }
}
