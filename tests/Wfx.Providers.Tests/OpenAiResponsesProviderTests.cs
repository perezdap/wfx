using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wfx.Core;
using Wfx.Providers;

namespace Wfx.Providers.Tests;

public sealed class OpenAiResponsesProviderTests
{
    [Fact]
    public async Task PostsToTheResponsesEndpointAsAStatelessStream()
    {
        var handler = TextStream();
        var provider = CreateProvider(handler);

        await CollectAsync(provider.StreamAsync(
            new ModelRequest("test-model", [new ModelMessage(ModelRole.User, "hi")], []),
            TestContext.Current.CancellationToken));

        Assert.Equal("https://example.test/v1/responses", handler.RequestUri!.ToString());
        using var body = JsonDocument.Parse(handler.RequestBody);
        Assert.Equal("test-model", body.RootElement.GetProperty("model").GetString());
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
        Assert.False(body.RootElement.GetProperty("store").GetBoolean());
        Assert.False(body.RootElement.TryGetProperty("messages", out _));
    }

    [Fact]
    public async Task AsksForReasoningContentThatAStatelessClientMustReplay()
    {
        var handler = TextStream();
        var provider = CreateProvider(handler);

        await CollectAsync(provider.StreamAsync(
            new ModelRequest("test-model", [new ModelMessage(ModelRole.User, "hi")], []),
            TestContext.Current.CancellationToken));

        using var body = JsonDocument.Parse(handler.RequestBody);
        var include = body.RootElement.GetProperty("include").EnumerateArray().Select(static value => value.GetString()).ToArray();
        Assert.Contains("reasoning.encrypted_content", include);
    }

    [Fact]
    public async Task KeepsTheFinishedTurnAsTheEndpointSentIt()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
            data: {"type":"response.output_item.done","output_index":0,"item":{"type":"reasoning","id":"rs-1","encrypted_content":"opaque-blob","summary":[]}}

            data: {"type":"response.output_item.done","output_index":1,"item":{"type":"function_call","id":"fc-1","call_id":"call-1","name":"read_file","arguments":"{\"path\":\"README.md\"}"}}

            data: {"type":"response.completed","response":{"id":"resp-1"}}

            """, "text/event-stream");
        var provider = CreateProvider(handler);

        var events = await CollectAsync(provider.StreamAsync(
            new ModelRequest("test-model", [new ModelMessage(ModelRole.User, "inspect")], []),
            TestContext.Current.CancellationToken));

        var message = Assert.Single(events.OfType<ModelCompleted>()).Message;
        using var items = JsonDocument.Parse(message.ProviderItemsJson!);
        var recorded = items.RootElement.EnumerateArray().ToArray();
        Assert.Equal(2, recorded.Length);
        Assert.Equal("reasoning", recorded[0].GetProperty("type").GetString());
        Assert.Equal("opaque-blob", recorded[0].GetProperty("encrypted_content").GetString());
        Assert.Equal("function_call", recorded[1].GetProperty("type").GetString());
        Assert.Equal("call-1", Assert.Single(message.ToolCalls!).Id);
    }

    [Fact]
    public async Task ReplaysReasoningItemsOnTheFollowingTurn()
    {
        var handler = TextStream();
        var provider = CreateProvider(handler);
        var request = new ModelRequest(
            "test-model",
            [
                new ModelMessage(ModelRole.User, "inspect"),
                new ModelMessage(
                    ModelRole.Assistant,
                    null,
                    [new ModelToolCall("call-1", "read_file", "{\"path\":\"README.md\"}")],
                    ProviderItemsJson: """
                        [{"type":"reasoning","id":"rs-1","encrypted_content":"opaque-blob","summary":[]},
                         {"type":"function_call","id":"fc-1","call_id":"call-1","name":"read_file","arguments":"{\"path\":\"README.md\"}"}]
                        """),
                new ModelMessage(ModelRole.Tool, "# wfx", ToolCallId: "call-1")
            ],
            []);

        await CollectAsync(provider.StreamAsync(request, TestContext.Current.CancellationToken));

        using var body = JsonDocument.Parse(handler.RequestBody);
        var input = body.RootElement.GetProperty("input").EnumerateArray().ToArray();
        Assert.Equal(4, input.Length);
        Assert.Equal("reasoning", input[1].GetProperty("type").GetString());
        Assert.Equal("opaque-blob", input[1].GetProperty("encrypted_content").GetString());
        Assert.Equal("fc-1", input[2].GetProperty("id").GetString());
        Assert.Equal("function_call_output", input[3].GetProperty("type").GetString());
    }

    [Fact]
    public async Task RetriesOnceWithoutProviderItemsWhenEncryptedContentIsRejected()
    {
        var handler = new SequenceStubHandler([
            new StubResponse(
                HttpStatusCode.BadRequest,
                """{"error":{"code":"invalid_encrypted_content","message":"encrypted content is invalid"}}""",
                "application/json"),
            new StubResponse(HttpStatusCode.OK, """
                data: {"type":"response.output_text.delta","item_id":"msg-1","output_index":0,"delta":"recovered"}

                data: {"type":"response.completed","response":{"id":"resp-1"}}

                """, "text/event-stream")
        ]);
        var provider = CreateProvider(handler);
        var request = new ModelRequest(
            "test-model",
            [
                new ModelMessage(ModelRole.User, "before"),
                new ModelMessage(
                    ModelRole.Assistant,
                    "previous answer",
                    ProviderItemsJson: """[{"type":"reasoning","id":"rs-1","encrypted_content":"opaque"}]"""),
                new ModelMessage(ModelRole.User, "continue")
            ],
            []);

        var events = await CollectAsync(provider.StreamAsync(request, TestContext.Current.CancellationToken));

        Assert.Equal("recovered", Assert.Single(events.OfType<ModelCompleted>()).Message.Content);
        Assert.Equal(2, handler.RequestBodies.Count);
        using var first = JsonDocument.Parse(handler.RequestBodies[0]);
        using var second = JsonDocument.Parse(handler.RequestBodies[1]);
        Assert.Contains(
            first.RootElement.GetProperty("input").EnumerateArray(),
            static item => item.TryGetProperty("encrypted_content", out _));
        Assert.DoesNotContain(
            second.RootElement.GetProperty("input").EnumerateArray(),
            static item => item.TryGetProperty("encrypted_content", out _));
        var rebuilt = Assert.Single(
            second.RootElement.GetProperty("input").EnumerateArray(),
            static item => item.TryGetProperty("role", out var role) && role.GetString() == "assistant");
        Assert.Equal("previous answer", rebuilt.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task DoesNotDowngradeOtherBadRequests()
    {
        var handler = new SequenceStubHandler([
            new StubResponse(
                HttpStatusCode.BadRequest,
                """{"error":{"code":"invalid_request_error","message":"bad input"}}""",
                "application/json"),
            new StubResponse(HttpStatusCode.OK, string.Empty, "text/event-stream")
        ]);
        var provider = CreateProvider(handler);
        var request = new ModelRequest(
            "test-model",
            [new ModelMessage(ModelRole.Assistant, "answer", ProviderItemsJson: """[{"type":"reasoning"}]""")],
            []);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await CollectAsync(provider.StreamAsync(request, TestContext.Current.CancellationToken)));

        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Single(handler.RequestBodies);
    }

    [Fact]
    public async Task DoesNotDowngradeItemsNotPersistedErrors()
    {
        var handler = new SequenceStubHandler([
            new StubResponse(
                HttpStatusCode.NotFound,
                """{"error":{"code":"not_found","message":"Items are not persisted when store is set to false"}}""",
                "application/json"),
            new StubResponse(HttpStatusCode.OK, string.Empty, "text/event-stream")
        ]);
        var provider = CreateProvider(handler);
        var request = new ModelRequest(
            "test-model",
            [new ModelMessage(ModelRole.Assistant, "answer", ProviderItemsJson: """[{"type":"reasoning"}]""")],
            []);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await CollectAsync(provider.StreamAsync(request, TestContext.Current.CancellationToken)));

        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
        Assert.Contains("Items are not persisted", exception.Message);
        Assert.Single(handler.RequestBodies);
    }

    [Fact]
    public async Task RetriesEncryptedContentRejectionOnlyOnce()
    {
        var rejection = new StubResponse(
            HttpStatusCode.BadRequest,
            """{"error":{"code":"invalid_encrypted_content","message":"encrypted content is invalid"}}""",
            "application/json");
        var handler = new SequenceStubHandler([
            rejection,
            rejection,
            new StubResponse(HttpStatusCode.OK, string.Empty, "text/event-stream")
        ]);
        var provider = CreateProvider(handler);
        var request = new ModelRequest(
            "test-model",
            [new ModelMessage(ModelRole.Assistant, "answer", ProviderItemsJson: """[{"type":"reasoning"}]""")],
            []);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await CollectAsync(provider.StreamAsync(request, TestContext.Current.CancellationToken)));

        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Equal(2, handler.RequestBodies.Count);
    }

    [Fact]
    public async Task ResumedConversationUnderAnotherModelDowngradesAtTheAgentLoopSeam()
    {
        const string providerItems = """[{"type":"reasoning","id":"rs-1","encrypted_content":"opaque"}]""";
        var handler = new SequenceStubHandler([
            new StubResponse(
                HttpStatusCode.BadRequest,
                """{"error":{"code":"invalid_encrypted_content","message":"encrypted content is invalid"}}""",
                "application/json"),
            new StubResponse(HttpStatusCode.OK, """
                data: {"type":"response.output_text.delta","item_id":"msg-1","output_index":0,"delta":"continued"}

                data: {"type":"response.completed","response":{"id":"resp-1"}}

                """, "text/event-stream")
        ]);
        var provider = CreateProvider(handler);
        var resumedMessages = new ModelMessage[]
        {
            new(ModelRole.System, "instructions"),
            new(ModelRole.User, "first question"),
            new(ModelRole.Assistant, "first answer", ProviderItemsJson: providerItems)
        };
        var agent = new Agent(
            provider,
            new ToolRegistry([]),
            new PolicyApprovalService(ApprovalMode.Never, static (_, _) => ValueTask.FromResult(false)),
            new StaticContextProvider("unused"),
            new SilentObserver(),
            new AgentOptions(new EndpointIdentity("new-profile", "openai", "responses", "new-model")),
            Path.GetTempPath(),
            resumedMessages);

        var result = await agent.RunAsync("second question", TestContext.Current.CancellationToken);

        Assert.Equal("continued", result.FinalResponse);
        Assert.Equal(providerItems, result.Messages[2].ProviderItemsJson);
        Assert.Equal(2, handler.RequestBodies.Count);
        using var first = JsonDocument.Parse(handler.RequestBodies[0]);
        using var second = JsonDocument.Parse(handler.RequestBodies[1]);
        Assert.Equal("new-model", first.RootElement.GetProperty("model").GetString());
        Assert.Contains(
            first.RootElement.GetProperty("input").EnumerateArray(),
            static item => item.TryGetProperty("encrypted_content", out _));
        Assert.DoesNotContain(
            second.RootElement.GetProperty("input").EnumerateArray(),
            static item => item.TryGetProperty("encrypted_content", out _));
    }

    [Fact]
    public async Task RebuildsTheTurnWhenNoProviderItemsWereRecorded()
    {
        var handler = TextStream();
        var provider = CreateProvider(handler);
        var request = new ModelRequest(
            "test-model",
            [
                new ModelMessage(ModelRole.User, "inspect"),
                new ModelMessage(ModelRole.Assistant, "on it", ProviderItemsJson: "[]")
            ],
            []);

        await CollectAsync(provider.StreamAsync(request, TestContext.Current.CancellationToken));

        using var body = JsonDocument.Parse(handler.RequestBody);
        var input = body.RootElement.GetProperty("input").EnumerateArray().ToArray();
        Assert.Equal(2, input.Length);
        Assert.Equal("message", input[1].GetProperty("type").GetString());
        Assert.Equal("on it", input[1].GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task MapsConversationStateToInputItems()
    {
        var handler = TextStream();
        var provider = CreateProvider(handler);
        var request = new ModelRequest(
            "test-model",
            [
                new ModelMessage(ModelRole.System, "be brief"),
                new ModelMessage(ModelRole.User, "read the readme"),
                new ModelMessage(
                    ModelRole.Assistant,
                    "on it",
                    [new ModelToolCall("call-1", "read_file", "{\"path\":\"README.md\"}")]),
                new ModelMessage(ModelRole.Tool, "# wfx", ToolCallId: "call-1")
            ],
            []);

        await CollectAsync(provider.StreamAsync(request, TestContext.Current.CancellationToken));

        using var body = JsonDocument.Parse(handler.RequestBody);
        var input = body.RootElement.GetProperty("input").EnumerateArray().ToArray();
        Assert.Equal(5, input.Length);

        Assert.Equal("message", input[0].GetProperty("type").GetString());
        Assert.Equal("system", input[0].GetProperty("role").GetString());
        Assert.Equal("input_text", input[0].GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal("be brief", input[0].GetProperty("content")[0].GetProperty("text").GetString());

        Assert.Equal("user", input[1].GetProperty("role").GetString());
        Assert.Equal("input_text", input[1].GetProperty("content")[0].GetProperty("type").GetString());

        Assert.Equal("assistant", input[2].GetProperty("role").GetString());
        Assert.Equal("output_text", input[2].GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal("on it", input[2].GetProperty("content")[0].GetProperty("text").GetString());

        Assert.Equal("function_call", input[3].GetProperty("type").GetString());
        Assert.Equal("call-1", input[3].GetProperty("call_id").GetString());
        Assert.Equal("read_file", input[3].GetProperty("name").GetString());
        Assert.Equal("{\"path\":\"README.md\"}", input[3].GetProperty("arguments").GetString());

        Assert.Equal("function_call_output", input[4].GetProperty("type").GetString());
        Assert.Equal("call-1", input[4].GetProperty("call_id").GetString());
        Assert.Equal("# wfx", input[4].GetProperty("output").GetString());
    }

    [Fact]
    public async Task OmitsAnAssistantMessageItemWhenTheTurnWasToolCallsOnly()
    {
        var handler = TextStream();
        var provider = CreateProvider(handler);
        var request = new ModelRequest(
            "test-model",
            [
                new ModelMessage(ModelRole.User, "go"),
                new ModelMessage(ModelRole.Assistant, null, [new ModelToolCall("call-1", "list_directory", "{}")])
            ],
            []);

        await CollectAsync(provider.StreamAsync(request, TestContext.Current.CancellationToken));

        using var body = JsonDocument.Parse(handler.RequestBody);
        var input = body.RootElement.GetProperty("input").EnumerateArray().ToArray();
        Assert.Equal(2, input.Length);
        Assert.Equal("function_call", input[1].GetProperty("type").GetString());
    }

    [Fact]
    public async Task SerializesToolDefinitionsAsFlatFunctionTools()
    {
        var handler = TextStream();
        var provider = CreateProvider(handler);
        var request = new ModelRequest(
            "test-model",
            [new ModelMessage(ModelRole.User, "hi")],
            [new ToolDefinition("sample", "Sample tool.", new JsonObject { ["type"] = "object" })]);

        await CollectAsync(provider.StreamAsync(request, TestContext.Current.CancellationToken));

        using var body = JsonDocument.Parse(handler.RequestBody);
        var tool = Assert.Single(body.RootElement.GetProperty("tools").EnumerateArray().ToArray());
        Assert.Equal("function", tool.GetProperty("type").GetString());
        Assert.Equal("sample", tool.GetProperty("name").GetString());
        Assert.Equal("Sample tool.", tool.GetProperty("description").GetString());
        Assert.Equal("object", tool.GetProperty("parameters").GetProperty("type").GetString());
        Assert.False(tool.TryGetProperty("function", out _));
    }

    [Fact]
    public async Task StreamsTextDeltasAndReportsUsage()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
            event: response.output_text.delta
            data: {"type":"response.output_text.delta","item_id":"msg-1","output_index":0,"delta":"hello "}

            event: response.output_text.delta
            data: {"type":"response.output_text.delta","item_id":"msg-1","output_index":0,"delta":"world"}

            event: response.completed
            data: {"type":"response.completed","response":{"id":"resp-1","usage":{"input_tokens":11,"output_tokens":3}}}

            """, "text/event-stream");
        var provider = CreateProvider(handler);

        var events = await CollectAsync(provider.StreamAsync(
            new ModelRequest("test-model", [new ModelMessage(ModelRole.User, "hi")], []),
            TestContext.Current.CancellationToken));

        Assert.Equal(["hello ", "world"], events.OfType<ModelTextDelta>().Select(static delta => delta.Text).ToArray());
        var completed = Assert.Single(events.OfType<ModelCompleted>());
        Assert.Equal("hello world", completed.Message.Content);
        Assert.Null(completed.Message.ToolCalls);
        Assert.Equal(11, completed.Usage!.InputTokens);
        Assert.Equal(3, completed.Usage.OutputTokens);
    }

    [Fact]
    public async Task ReassemblesAnnouncedToolCallsFromArgumentDeltas()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
            data: {"type":"response.output_item.added","output_index":0,"item":{"type":"function_call","id":"fc-1","call_id":"call-1","name":"read_file","arguments":""}}

            data: {"type":"response.function_call_arguments.delta","item_id":"fc-1","output_index":0,"delta":"{\"pa"}

            data: {"type":"response.function_call_arguments.delta","item_id":"fc-1","output_index":0,"delta":"th\":\"README.md\"}"}

            data: {"type":"response.function_call_arguments.done","item_id":"fc-1","output_index":0,"arguments":"{\"path\":\"README.md\"}"}

            data: {"type":"response.completed","response":{"id":"resp-1"}}

            """, "text/event-stream");
        var provider = CreateProvider(handler);

        var events = await CollectAsync(provider.StreamAsync(
            new ModelRequest("test-model", [new ModelMessage(ModelRole.User, "inspect")], []),
            TestContext.Current.CancellationToken));

        var completed = Assert.Single(events.OfType<ModelCompleted>());
        var call = Assert.Single(completed.Message.ToolCalls!);
        Assert.Equal("call-1", call.Id);
        Assert.Equal("read_file", call.Name);
        Assert.Equal("{\"path\":\"README.md\"}", call.ArgumentsJson);
    }

    [Fact]
    public async Task PrefersTheCompletedItemArgumentsOverStreamedDeltas()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
            data: {"type":"response.output_item.added","output_index":0,"item":{"type":"function_call","id":"fc-1","call_id":"call-1","name":"read_file","arguments":""}}

            data: {"type":"response.function_call_arguments.delta","item_id":"fc-1","output_index":0,"delta":"{\"path\":"}

            data: {"type":"response.output_item.done","output_index":0,"item":{"type":"function_call","id":"fc-1","call_id":"call-1","name":"read_file","arguments":"{\"path\":\"AGENTS.md\"}"}}

            data: {"type":"response.completed","response":{"id":"resp-1"}}

            """, "text/event-stream");
        var provider = CreateProvider(handler);

        var events = await CollectAsync(provider.StreamAsync(
            new ModelRequest("test-model", [new ModelMessage(ModelRole.User, "inspect")], []),
            TestContext.Current.CancellationToken));

        var call = Assert.Single(Assert.Single(events.OfType<ModelCompleted>()).Message.ToolCalls!);
        Assert.Equal("{\"path\":\"AGENTS.md\"}", call.ArgumentsJson);
    }

    [Fact]
    public async Task OrdersMultipleToolCallsByOutputIndex()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
            data: {"type":"response.output_item.added","output_index":1,"item":{"type":"function_call","id":"fc-2","call_id":"call-2","name":"second","arguments":""}}

            data: {"type":"response.output_item.added","output_index":0,"item":{"type":"function_call","id":"fc-1","call_id":"call-1","name":"first","arguments":""}}

            data: {"type":"response.function_call_arguments.done","item_id":"fc-2","output_index":1,"arguments":"{\"b\":2}"}

            data: {"type":"response.function_call_arguments.done","item_id":"fc-1","output_index":0,"arguments":"{\"a\":1}"}

            data: {"type":"response.completed","response":{"id":"resp-1"}}

            """, "text/event-stream");
        var provider = CreateProvider(handler);

        var events = await CollectAsync(provider.StreamAsync(
            new ModelRequest("test-model", [new ModelMessage(ModelRole.User, "both")], []),
            TestContext.Current.CancellationToken));

        var calls = Assert.Single(events.OfType<ModelCompleted>()).Message.ToolCalls!;
        Assert.Equal(["first", "second"], calls.Select(static call => call.Name).ToArray());
        Assert.Equal("{\"a\":1}", calls[0].ArgumentsJson);
    }

    [Fact]
    public async Task RejectsFunctionCallEventsWithoutAnOutputIndex()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
            data: {"type":"response.output_item.added","output_index":0,"item":{"type":"function_call","id":"fc-1","call_id":"call-1","name":"read_file","arguments":""}}

            data: {"type":"response.function_call_arguments.delta","item_id":"fc-1","delta":"{}"}

            data: {"type":"response.completed","response":{"id":"resp-1"}}

            """, "text/event-stream");
        var provider = CreateProvider(handler);

        await Assert.ThrowsAsync<ProviderProtocolException>(async () =>
            await CollectAsync(provider.StreamAsync(
                new ModelRequest("test-model", [new ModelMessage(ModelRole.User, "inspect")], []),
                TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task KeepsWholeArgumentsAnnouncedWithTheToolCall()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
            data: {"type":"response.output_item.added","output_index":0,"item":{"type":"function_call","id":"fc-1","call_id":"call-1","name":"read_file","arguments":"{\"path\":\"AGENTS.md\"}"}}

            data: {"type":"response.completed","response":{"id":"resp-1"}}

            """, "text/event-stream");
        var provider = CreateProvider(handler);

        var events = await CollectAsync(provider.StreamAsync(
            new ModelRequest("test-model", [new ModelMessage(ModelRole.User, "inspect")], []),
            TestContext.Current.CancellationToken));

        var call = Assert.Single(Assert.Single(events.OfType<ModelCompleted>()).Message.ToolCalls!);
        Assert.Equal("{\"path\":\"AGENTS.md\"}", call.ArgumentsJson);
    }

    [Fact]
    public async Task ReplacesAnnouncedArgumentsWithStreamedDeltas()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
            data: {"type":"response.output_item.added","output_index":0,"item":{"type":"function_call","id":"fc-1","call_id":"call-1","name":"read_file","arguments":"{}"}}

            data: {"type":"response.function_call_arguments.delta","item_id":"fc-1","output_index":0,"delta":"{\"path\":"}

            data: {"type":"response.function_call_arguments.delta","item_id":"fc-1","output_index":0,"delta":"\"README.md\"}"}

            data: {"type":"response.completed","response":{"id":"resp-1"}}

            """, "text/event-stream");
        var provider = CreateProvider(handler);

        var events = await CollectAsync(provider.StreamAsync(
            new ModelRequest("test-model", [new ModelMessage(ModelRole.User, "inspect")], []),
            TestContext.Current.CancellationToken));

        var call = Assert.Single(Assert.Single(events.OfType<ModelCompleted>()).Message.ToolCalls!);
        Assert.Equal("{\"path\":\"README.md\"}", call.ArgumentsJson);
    }

    [Fact]
    public async Task RejectsTruncatedResponsesWithTheReportedReason()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
            data: {"type":"response.output_text.delta","item_id":"msg-1","output_index":0,"delta":"half an ans"}

            data: {"type":"response.incomplete","response":{"id":"resp-1","incomplete_details":{"reason":"max_output_tokens"}}}

            """, "text/event-stream");
        var provider = CreateProvider(handler);

        var exception = await Assert.ThrowsAsync<ProviderProtocolException>(async () =>
            await CollectAsync(provider.StreamAsync(
                new ModelRequest("test-model", [new ModelMessage(ModelRole.User, "x")], []),
                TestContext.Current.CancellationToken)));

        Assert.Contains("incomplete response", exception.Message);
        Assert.Contains("max_output_tokens", exception.Message);
    }

    [Fact]
    public async Task RejectsMalformedStreamingJson()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "data: {not-json}\n\n", "text/event-stream");
        var provider = CreateProvider(handler);

        var exception = await Assert.ThrowsAsync<ProviderProtocolException>(async () =>
            await CollectAsync(provider.StreamAsync(
                new ModelRequest("model", [new ModelMessage(ModelRole.User, "x")], []),
                TestContext.Current.CancellationToken)));

        Assert.Contains("malformed streaming JSON", exception.Message);
    }

    [Fact]
    public async Task RejectsStreamEventsWithoutAType()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
            data: {"delta":"hello"}

            """, "text/event-stream");
        var provider = CreateProvider(handler);

        await Assert.ThrowsAsync<ProviderProtocolException>(async () =>
            await CollectAsync(provider.StreamAsync(
                new ModelRequest("model", [new ModelMessage(ModelRole.User, "x")], []),
                TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task RejectsAnEmptyStream()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "\n\n", "text/event-stream");
        var provider = CreateProvider(handler);

        await Assert.ThrowsAsync<ProviderProtocolException>(async () =>
            await CollectAsync(provider.StreamAsync(
                new ModelRequest("model", [new ModelMessage(ModelRole.User, "x")], []),
                TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task RejectsAnIncompleteToolCall()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
            data: {"type":"response.output_item.added","output_index":0,"item":{"type":"function_call","id":"fc-1","arguments":""}}

            data: {"type":"response.completed","response":{"id":"resp-1"}}

            """, "text/event-stream");
        var provider = CreateProvider(handler);

        var exception = await Assert.ThrowsAsync<ProviderProtocolException>(async () =>
            await CollectAsync(provider.StreamAsync(
                new ModelRequest("model", [new ModelMessage(ModelRole.User, "x")], []),
                TestContext.Current.CancellationToken)));

        Assert.Contains("incomplete tool call", exception.Message);
    }

    [Fact]
    public async Task RejectsMalformedToolCallArguments()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
            data: {"type":"response.output_item.added","output_index":0,"item":{"type":"function_call","id":"fc-1","call_id":"call-1","name":"read_file","arguments":""}}

            data: {"type":"response.function_call_arguments.done","item_id":"fc-1","output_index":0,"arguments":"{\"path\":"}

            data: {"type":"response.completed","response":{"id":"resp-1"}}

            """, "text/event-stream");
        var provider = CreateProvider(handler);

        var exception = await Assert.ThrowsAsync<ProviderProtocolException>(async () =>
            await CollectAsync(provider.StreamAsync(
                new ModelRequest("model", [new ModelMessage(ModelRole.User, "x")], []),
                TestContext.Current.CancellationToken)));

        Assert.Contains("malformed tool-call arguments", exception.Message);
    }

    [Fact]
    public async Task SurfacesFailedResponseEvents()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
            data: {"type":"response.failed","response":{"id":"resp-1","error":{"code":"server_error","message":"the model gave up"}}}

            """, "text/event-stream");
        var provider = CreateProvider(handler);

        var exception = await Assert.ThrowsAsync<ProviderProtocolException>(async () =>
            await CollectAsync(provider.StreamAsync(
                new ModelRequest("model", [new ModelMessage(ModelRole.User, "x")], []),
                TestContext.Current.CancellationToken)));

        Assert.Contains("the model gave up", exception.Message);
    }

    [Fact]
    public async Task SurfacesStreamErrorEvents()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
            data: {"type":"error","code":"rate_limit_exceeded","message":"slow down"}

            """, "text/event-stream");
        var provider = CreateProvider(handler);

        var exception = await Assert.ThrowsAsync<ProviderProtocolException>(async () =>
            await CollectAsync(provider.StreamAsync(
                new ModelRequest("model", [new ModelMessage(ModelRole.User, "x")], []),
                TestContext.Current.CancellationToken)));

        Assert.Contains("slow down", exception.Message);
    }

    [Fact]
    public async Task RejectsAStreamThatNeverCompletes()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
            data: {"type":"response.output_text.delta","item_id":"msg-1","output_index":0,"delta":"partial"}

            """, "text/event-stream");
        var provider = CreateProvider(handler);

        var exception = await Assert.ThrowsAsync<ProviderProtocolException>(async () =>
            await CollectAsync(provider.StreamAsync(
                new ModelRequest("model", [new ModelMessage(ModelRole.User, "x")], []),
                TestContext.Current.CancellationToken)));

        Assert.Contains("without a completion event", exception.Message);
    }

    [Fact]
    public async Task IgnoresUnrelatedLifecycleEvents()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """
            data: {"type":"response.created","response":{"id":"resp-1"}}

            data: {"type":"response.in_progress","response":{"id":"resp-1"}}

            data: {"type":"response.content_part.added","item_id":"msg-1","output_index":0,"content_index":0,"part":{"type":"output_text","text":""}}

            data: {"type":"response.output_text.delta","item_id":"msg-1","output_index":0,"delta":"ok"}

            data: {"type":"response.output_text.done","item_id":"msg-1","output_index":0,"text":"ok"}

            data: {"type":"response.completed","response":{"id":"resp-1"}}

            """, "text/event-stream");
        var provider = CreateProvider(handler);

        var events = await CollectAsync(provider.StreamAsync(
            new ModelRequest("model", [new ModelMessage(ModelRole.User, "x")], []),
            TestContext.Current.CancellationToken));

        Assert.Equal("ok", Assert.Single(events.OfType<ModelCompleted>()).Message.Content);
    }

    [Fact]
    public async Task RedactsSecretsFromEndpointErrors()
    {
        const string secret = "responses-secret-value";
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
    public async Task PropagatesCancellationRequestedBeforeTheRequest()
    {
        var provider = CreateProvider(TextStream());
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await CollectAsync(provider.StreamAsync(
                new ModelRequest("model", [new ModelMessage(ModelRole.User, "x")], []),
                cancellation.Token)));
    }

    [Fact]
    public async Task PropagatesCancellationRequestedMidStream()
    {
        var handler = new BlockingStreamHandler("""
            data: {"type":"response.output_text.delta","item_id":"msg-1","output_index":0,"delta":"first"}


            """);
        var provider = CreateProvider(handler);
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var streamEvent in provider.StreamAsync(
                new ModelRequest("model", [new ModelMessage(ModelRole.User, "x")], []),
                cancellation.Token))
            {
                if (streamEvent is ModelTextDelta)
                {
                    await cancellation.CancelAsync();
                }
            }
        });
    }

    private sealed class SilentObserver : IAgentObserver
    {
    }

    private static StubHandler TextStream() => new(HttpStatusCode.OK, """
        data: {"type":"response.output_text.delta","item_id":"msg-1","output_index":0,"delta":"ok"}

        data: {"type":"response.completed","response":{"id":"resp-1"}}

        """, "text/event-stream");

    private static OpenAiResponsesProvider CreateProvider(
        HttpMessageHandler handler,
        string? apiKey = null,
        IReadOnlyDictionary<string, string>? headers = null) =>
        new(new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan }, new OpenAiProviderOptions
        {
            BaseUri = new Uri("https://example.test/v1"),
            ApiKey = apiKey,
            Headers = headers ?? new Dictionary<string, string>(),
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
}
