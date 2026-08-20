using System.Net;
using System.Text;
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

    private static OpenAiCompatibleProvider CreateProvider(StubHandler handler, string? apiKey = null) =>
        new(new HttpClient(handler), new OpenAiProviderOptions
        {
            BaseUri = new Uri("https://example.test/v1"),
            ApiKey = apiKey,
            Timeout = TimeSpan.FromSeconds(10)
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

    private sealed class StubHandler(HttpStatusCode statusCode, string content, string mediaType) : HttpMessageHandler
    {
        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content, Encoding.UTF8, mediaType)
            };
        }
    }
}
