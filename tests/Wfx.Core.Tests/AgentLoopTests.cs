using System.Runtime.CompilerServices;
using System.Text.Json;
using Wfx.Core;

namespace Wfx.Core.Tests;

public sealed class AgentLoopTests
{
    [Fact]
    public async Task RunsToolAndFeedsStructuredResultBackToModel()
    {
        using var workspace = new TemporaryDirectory();
        var model = new SequenceModelProvider([
            new ModelMessage(ModelRole.Assistant, null,
            [new ModelToolCall("call-1", "echo", "{\"value\":\"hello\"}")]),
            new ModelMessage(ModelRole.Assistant, "finished")
        ]);
        var tool = new EchoTool();
        var agent = new Agent(
            model,
            new ToolRegistry([tool]),
            new PolicyApprovalService(ApprovalMode.Workspace, static (_, _) => ValueTask.FromResult(false)),
            new StaticContextProvider("test context"),
            new SilentObserver(),
            new AgentOptions("fake-model"),
            workspace.Path);

        var result = await agent.RunAsync("do it", TestContext.Current.CancellationToken);

        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal("finished", result.FinalResponse);
        Assert.Equal(2, result.Iterations);
        Assert.Equal(1, tool.ExecutionCount);
        var secondRequest = model.Requests[1];
        var toolMessage = Assert.Single(secondRequest.Messages, static message => message.Role == ModelRole.Tool);
        Assert.Contains("\"success\":true", toolMessage.Content!);
        Assert.Contains("echo:hello", toolMessage.Content!);
    }

    [Fact]
    public async Task ReplaysProviderItemsFromTheAssistantTurnBackToTheModel()
    {
        using var workspace = new TemporaryDirectory();
        const string providerItems = """[{"type":"reasoning","id":"rs-1","encrypted_content":"opaque-blob"}]""";
        var model = new SequenceModelProvider([
            new ModelMessage(
                ModelRole.Assistant,
                null,
                [new ModelToolCall("call-1", "echo", "{\"value\":\"hello\"}")],
                ProviderItemsJson: providerItems),
            new ModelMessage(ModelRole.Assistant, "finished")
        ]);
        var agent = new Agent(
            model,
            new ToolRegistry([new EchoTool()]),
            new PolicyApprovalService(ApprovalMode.Workspace, static (_, _) => ValueTask.FromResult(false)),
            new StaticContextProvider("test context"),
            new SilentObserver(),
            new AgentOptions("fake-model"),
            workspace.Path);

        await agent.RunAsync("do it", TestContext.Current.CancellationToken);

        var assistant = model.Requests[1].Messages.First(static message => message.Role == ModelRole.Assistant);
        Assert.Equal(providerItems, assistant.ProviderItemsJson);
        Assert.Equal("call-1", Assert.Single(assistant.ToolCalls!).Id);
    }

    [Fact]
    public async Task ReportsToolArgumentsToTheObserver()
    {
        using var workspace = new TemporaryDirectory();
        var model = new SequenceModelProvider([
            new ModelMessage(ModelRole.Assistant, null,
            [new ModelToolCall("call-1", "echo", "{\"value\":\"hello\"}")]),
            new ModelMessage(ModelRole.Assistant, "finished")
        ]);
        var observer = new RecordingObserver();
        var agent = new Agent(
            model,
            new ToolRegistry([new EchoTool()]),
            new PolicyApprovalService(ApprovalMode.Workspace, static (_, _) => ValueTask.FromResult(false)),
            new StaticContextProvider("test context"),
            observer,
            new AgentOptions("fake-model"),
            workspace.Path);

        await agent.RunAsync("do it", TestContext.Current.CancellationToken);

        var started = Assert.Single(observer.Started);
        Assert.Equal("echo", started.Name);
        Assert.Equal("{\"value\":\"hello\"}", started.ArgumentsJson);
    }

    [Fact]
    public async Task EmitsTypedToolEventsWithModelCallIdsAndCompletionTotals()
    {
        using var workspace = new TemporaryDirectory();
        var model = new SequenceModelProvider([
            new ModelMessage(ModelRole.Assistant, null,
            [new ModelToolCall("provider-call-42", "echo", "{\"value\":\"hello\"}")]),
            new ModelMessage(ModelRole.Assistant, "finished")
        ], usage: new ModelUsage(10, 5));
        var observer = new TypedRecordingObserver();
        var agent = CreateAgent(
            model,
            workspace.Path,
            observer: observer,
            metadata: new AgentTurnMetadata("session-42", ApprovalMode.Workspace));

        await agent.RunAsync("do it", TestContext.Current.CancellationToken);

        var started = Assert.Single(observer.Events.OfType<ToolStartedEvent>());
        Assert.Equal("provider-call-42", started.CallId);
        Assert.Equal("echo", started.Name);
        Assert.Equal("{\"value\":\"hello\"}", started.ArgumentsJson);
        Assert.Equal(ApprovalLevel.ReadOnly, started.ApprovalLevel);

        var completed = Assert.Single(observer.Events.OfType<ToolCompletedEvent>());
        Assert.Equal("provider-call-42", completed.CallId);
        Assert.Equal("echo", completed.Name);
        Assert.True(completed.Result.Success);
        Assert.True(completed.Duration >= TimeSpan.Zero);

        var terminal = Assert.Single(observer.Events.OfType<TurnCompletedEvent>());
        Assert.Equal("session-42", terminal.SessionId);
        Assert.Equal(2, terminal.Iterations);
        Assert.Equal("finished", terminal.FinalMessage);
        Assert.Equal(new ModelUsage(20, 10), terminal.TotalUsage);
    }

    [Fact]
    public void ExplicitEventSerializerWritesContractShapesAndRawProviderItems()
    {
        var at = new DateTimeOffset(2026, 8, 22, 15, 4, 5, TimeSpan.Zero);
        var message = new MessageEvent(
            new ModelMessage(
                ModelRole.Assistant,
                "calling",
                [new ModelToolCall("call-1", "echo", "{\"value\":\"hello\"}")],
                ProviderItemsJson: """[{"type":"reasoning","id":"rs-1"}]"""),
            at);

        using var document = JsonDocument.Parse(Serialize(message));
        var root = document.RootElement;
        Assert.Equal("message", root.GetProperty("event").GetString());
        Assert.False(root.TryGetProperty("schema_version", out _));
        Assert.Equal(JsonValueKind.Array, root.GetProperty("provider_items").ValueKind);
        Assert.Equal("call-1", root.GetProperty("tool_calls")[0].GetProperty("id").GetString());

        var started = new ToolStartedEvent(
            "call-1",
            "echo",
            "{\"value\":\"hello\"}",
            ApprovalLevel.ReadOnly,
            at);
        using var startedDocument = JsonDocument.Parse(Serialize(started));
        Assert.Equal(
            "{\"value\":\"hello\"}",
            startedDocument.RootElement.GetProperty("arguments_json").GetString());
        Assert.Equal(JsonValueKind.String, startedDocument.RootElement.GetProperty("arguments_json").ValueKind);
    }

    [Fact]
    public async Task DeniedToolReturnsStructuredFailureAndDoesNotExecute()
    {
        using var workspace = new TemporaryDirectory();
        var model = new SequenceModelProvider([
            new ModelMessage(ModelRole.Assistant, null,
            [new ModelToolCall("call-1", "mutate", "{\"value\":\"hello\"}")]),
            new ModelMessage(ModelRole.Assistant, "stopped")
        ]);
        var tool = new MutatingTool();
        var agent = new Agent(
            model,
            new ToolRegistry([tool]),
            new PolicyApprovalService(ApprovalMode.Never, static (_, _) => ValueTask.FromResult(true)),
            new StaticContextProvider("test context"),
            new SilentObserver(),
            new AgentOptions("fake-model"),
            workspace.Path);

        var result = await agent.RunAsync("do it", TestContext.Current.CancellationToken);

        Assert.Equal("stopped", result.FinalResponse);
        Assert.Equal(2, result.Iterations);
        Assert.Equal(0, tool.ExecutionCount);
        var toolMessage = Assert.Single(model.Requests[1].Messages, static message => message.Role == ModelRole.Tool);
        Assert.Contains("\"success\":false", toolMessage.Content!);
        Assert.Contains("denied", toolMessage.Content!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("missing", "{\"value\":\"hello\"}", "Unknown tool")]
    [InlineData("echo", "{\"value\":", "Invalid JSON arguments")]
    public async Task ReportsRejectedToolCallsToTheObserver(string toolName, string argumentsJson, string expectedReason)
    {
        using var workspace = new TemporaryDirectory();
        var model = new SequenceModelProvider([
            new ModelMessage(ModelRole.Assistant, null,
            [new ModelToolCall("call-1", toolName, argumentsJson)]),
            new ModelMessage(ModelRole.Assistant, "stopped")
        ]);
        var observer = new RecordingObserver();
        var typed = new TypedRecordingObserver();
        var agent = new Agent(
            model,
            new ToolRegistry([new EchoTool()]),
            new PolicyApprovalService(ApprovalMode.Workspace, static (_, _) => ValueTask.FromResult(true)),
            new StaticContextProvider("test context"),
            new TestCompositeObserver(typed, observer),
            new AgentOptions("fake-model"),
            workspace.Path);

        await agent.RunAsync("do it", TestContext.Current.CancellationToken);

        Assert.Empty(observer.Started);
        var rejected = Assert.Single(observer.Rejected);
        Assert.Equal(toolName, rejected.Name);
        Assert.Equal(argumentsJson, rejected.ArgumentsJson);
        Assert.Contains(expectedReason, rejected.Reason, StringComparison.Ordinal);
        var typedRejected = Assert.Single(typed.Events.OfType<ToolRejectedEvent>());
        Assert.Equal("call-1", typedRejected.CallId);
        Assert.Equal(toolName, typedRejected.Name);
        Assert.Equal(argumentsJson, typedRejected.ArgumentsJson);
    }

    [Fact]
    public async Task ReportsApprovalDenialToTheObserver()
    {
        using var workspace = new TemporaryDirectory();
        var model = new SequenceModelProvider([
            new ModelMessage(ModelRole.Assistant, null,
            [new ModelToolCall("call-1", "mutate", "{\"value\":\"hello\"}")]),
            new ModelMessage(ModelRole.Assistant, "stopped")
        ]);
        var observer = new RecordingObserver();
        var agent = new Agent(
            model,
            new ToolRegistry([new MutatingTool()]),
            new PolicyApprovalService(ApprovalMode.Never, static (_, _) => ValueTask.FromResult(true)),
            new StaticContextProvider("test context"),
            observer,
            new AgentOptions("fake-model"),
            workspace.Path);

        await agent.RunAsync("do it", TestContext.Current.CancellationToken);

        Assert.Empty(observer.Started);
        var rejected = Assert.Single(observer.Rejected);
        Assert.Contains("denied", rejected.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NotifiesTurnStartWithEndpointIdentity()
    {
        using var workspace = new TemporaryDirectory();
        var model = new SequenceModelProvider([
            new ModelMessage(ModelRole.Assistant, "finished")
        ]);
        var observer = new RecordingObserver();
        var agent = CreateAgent(
            model,
            workspace.Path,
            observer: observer,
            options: new AgentOptions(new EndpointIdentity("work", "openrouter", "responses", "fake-model")));

        await agent.RunAsync("do it", TestContext.Current.CancellationToken);

        var endpoint = Assert.Single(observer.TurnStarts);
        Assert.Equal("work", endpoint.Profile);
        Assert.Equal("openrouter", endpoint.Provider);
        Assert.Equal("responses", endpoint.Protocol);
        Assert.Equal("fake-model", endpoint.Model);
    }

    [Fact]
    public async Task NotifiesEachAssistantAndToolResultMessage()
    {
        using var workspace = new TemporaryDirectory();
        const string providerItems = """[{"type":"reasoning","id":"rs-1","encrypted_content":"opaque"}]""";
        var model = new SequenceModelProvider([
            new ModelMessage(ModelRole.Assistant, "calling",
                [new ModelToolCall("call-1", "echo", "{\"value\":\"hello\"}")],
                ProviderItemsJson: providerItems),
            new ModelMessage(ModelRole.Assistant, "finished")
        ]);
        var observer = new RecordingObserver();
        var agent = CreateAgent(model, workspace.Path, observer: observer);

        await agent.RunAsync("do it", TestContext.Current.CancellationToken);

        Assert.Equal(5, observer.Messages.Count);
        Assert.Equal(ModelRole.System, observer.Messages[0].Role);
        Assert.Equal(ModelRole.User, observer.Messages[1].Role);
        Assert.Equal("do it", observer.Messages[1].Content);
        var first = observer.Messages[2];
        Assert.Equal(ModelRole.Assistant, first.Role);
        Assert.Equal("calling", first.Content);
        Assert.Equal("call-1", Assert.Single(first.ToolCalls!).Id);
        Assert.Equal(providerItems, first.ProviderItemsJson);
        var toolResult = observer.Messages[3];
        Assert.Equal(ModelRole.Tool, toolResult.Role);
        Assert.Equal("call-1", toolResult.ToolCallId);
        Assert.Equal("echo", toolResult.Name);
        Assert.Contains("echo:hello", toolResult.Content!);
        Assert.Equal("finished", observer.Messages[4].Content);
    }

    [Fact]
    public async Task NotifiesUsagePerModelCallNotPerTurn()
    {
        using var workspace = new TemporaryDirectory();
        var model = new SequenceModelProvider([
            new ModelMessage(ModelRole.Assistant, null,
            [new ModelToolCall("call-1", "echo", "{\"value\":\"a\"}")]),
            new ModelMessage(ModelRole.Assistant, "finished")
        ], usage: new ModelUsage(10, 5));
        var observer = new RecordingObserver();
        var agent = CreateAgent(model, workspace.Path, observer: observer);

        await agent.RunAsync("do it", TestContext.Current.CancellationToken);

        Assert.Equal(2, observer.Usage.Count);
        Assert.All(observer.Usage, static usage =>
        {
            Assert.Equal(10, usage.InputTokens);
            Assert.Equal(5, usage.OutputTokens);
        });
    }

    [Fact]
    public async Task EmitsTypedMaxIterationsInterruptionInsteadOfCompletion()
    {
        using var workspace = new TemporaryDirectory();
        var model = new SequenceModelProvider([
            new ModelMessage(ModelRole.Assistant, "partial",
            [new ModelToolCall("call-1", "echo", "{\"value\":\"a\"}")])
        ]);
        var observer = new TypedRecordingObserver();
        var agent = CreateAgent(
            model,
            workspace.Path,
            maxIterations: 1,
            observer: observer,
            metadata: new AgentTurnMetadata("session-limit", ApprovalMode.Never));

        var result = await agent.RunAsync("do it", TestContext.Current.CancellationToken);

        Assert.Equal(AgentRunStatus.IterationLimitReached, result.Status);
        var interrupted = Assert.Single(observer.Events.OfType<TurnInterruptedEvent>());
        Assert.Equal("session-limit", interrupted.SessionId);
        Assert.Equal(AgentInterruptionReason.MaxIterations, interrupted.Reason);
        Assert.Empty(observer.Events.OfType<TurnCompletedEvent>());
    }

    [Fact]
    public async Task EmitsTypedTimeoutInterruption()
    {
        using var workspace = new TemporaryDirectory();
        var observer = new TypedRecordingObserver();
        var agent = CreateAgent(
            new ThrowingModelProvider(new TimeoutException("provider timed out")),
            workspace.Path,
            observer: observer,
            metadata: new AgentTurnMetadata("session-timeout", ApprovalMode.Never));

        await Assert.ThrowsAsync<TimeoutException>(
            () => agent.RunAsync("do it", TestContext.Current.CancellationToken));

        var interrupted = Assert.Single(observer.Events.OfType<TurnInterruptedEvent>());
        Assert.Equal("session-timeout", interrupted.SessionId);
        Assert.Equal(AgentInterruptionReason.Timeout, interrupted.Reason);
        Assert.Empty(observer.Events.OfType<TurnErrorEvent>());
    }

    [Fact]
    public async Task NotifiesInterruptionAsADistinctOutcome()
    {
        using var workspace = new TemporaryDirectory();
        using var interruption = new CancellationTokenSource();
        var model = new SequenceModelProvider([
            new ModelMessage(ModelRole.Assistant, null,
            [new ModelToolCall("call-1", "interrupt", "{}")])
        ]);
        var observer = new RecordingObserver();
        var typed = new TypedRecordingObserver();
        var agent = CreateAgent(
            model,
            workspace.Path,
            observer: new TestCompositeObserver(typed, observer),
            tool: new InterruptingTool(interruption),
            metadata: new AgentTurnMetadata("session-cancelled", ApprovalMode.Never));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => agent.RunAsync("do it", interruption.Token));

        Assert.Equal(1, observer.Interruptions);
        Assert.Empty(observer.Errors);
        var interrupted = Assert.Single(typed.Events.OfType<TurnInterruptedEvent>());
        Assert.Equal("session-cancelled", interrupted.SessionId);
        Assert.Equal(AgentInterruptionReason.Cancelled, interrupted.Reason);
    }

    [Fact]
    public async Task NotifiesErrorAsADistinctOutcome()
    {
        using var workspace = new TemporaryDirectory();
        var observer = new RecordingObserver();
        var agent = CreateAgent(
            new ThrowingModelProvider(new InvalidOperationException("provider failed")),
            workspace.Path,
            observer: observer);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.RunAsync("do it", TestContext.Current.CancellationToken));

        Assert.Same(exception, Assert.Single(observer.Errors));
        Assert.Equal(0, observer.Interruptions);
    }

    [Fact]
    public async Task EmitsClassifiedTypedTurnErrorWithoutLosingLegacyException()
    {
        using var workspace = new TemporaryDirectory();
        var failure = new InvalidOperationException("provider failed");
        var typed = new TypedRecordingObserver();
        var legacy = new RecordingObserver();
        var observer = new TestCompositeObserver(typed, legacy);
        var agent = CreateAgent(
            new ThrowingModelProvider(failure),
            workspace.Path,
            observer: observer,
            metadata: new AgentTurnMetadata("session-error", ApprovalMode.Never));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.RunAsync("do it", TestContext.Current.CancellationToken));

        var error = Assert.Single(typed.Events.OfType<TurnErrorEvent>());
        Assert.Equal("session-error", error.SessionId);
        Assert.Equal(AgentErrorKind.ProviderError, error.Error.Kind);
        Assert.Equal("provider failed", error.Error.Message);
        Assert.Same(failure, Assert.Single(legacy.Errors));
    }

    [Fact]
    public async Task CancellationNotRequestedByTheCallerIsAnErrorNotAnInterruption()
    {
        using var workspace = new TemporaryDirectory();
        var observer = new RecordingObserver();
        var agent = CreateAgent(
            new ThrowingModelProvider(new OperationCanceledException("provider-internal timeout")),
            workspace.Path,
            observer: observer);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => agent.RunAsync("do it", TestContext.Current.CancellationToken));

        Assert.Single(observer.Errors);
        Assert.Equal(0, observer.Interruptions);
    }

    [Fact]
    public async Task IterationExhaustionReturnsPartialStateInsteadOfThrowing()
    {
        using var workspace = new TemporaryDirectory();
        var model = new SequenceModelProvider([
            new ModelMessage(ModelRole.Assistant, "step one",
            [new ModelToolCall("call-1", "echo", "{\"value\":\"a\"}")]),
            new ModelMessage(ModelRole.Assistant, "step two",
            [new ModelToolCall("call-2", "echo", "{\"value\":\"b\"}")])
        ]);
        var agent = CreateAgent(model, workspace.Path, maxIterations: 2);

        var result = await agent.RunAsync("do it", TestContext.Current.CancellationToken);

        Assert.Equal(AgentRunStatus.IterationLimitReached, result.Status);
        Assert.Equal(2, result.Iterations);
        Assert.Equal("step two", result.FinalResponse);
        Assert.Equal("step one\nstep two", result.AccumulatedText);
        Assert.NotNull(result.Note);
        Assert.Contains("iteration limit", result.Note, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, result.Messages.Count(static message => message.Role == ModelRole.Tool));
        Assert.Equal(2, result.Messages.Count(static message => message.Role == ModelRole.Assistant));
    }

    [Fact]
    public async Task IterationExhaustionIsDistinguishableFromCompletionAtSameIterationCount()
    {
        using var workspace = new TemporaryDirectory();
        var exhaustedModel = new SequenceModelProvider([
            new ModelMessage(ModelRole.Assistant, null,
            [new ModelToolCall("call-1", "echo", "{\"value\":\"a\"}")]),
            new ModelMessage(ModelRole.Assistant, null,
            [new ModelToolCall("call-2", "echo", "{\"value\":\"b\"}")])
        ]);
        var completedModel = new SequenceModelProvider([
            new ModelMessage(ModelRole.Assistant, null,
            [new ModelToolCall("call-1", "echo", "{\"value\":\"a\"}")]),
            new ModelMessage(ModelRole.Assistant, "finished")
        ]);

        var exhausted = await CreateAgent(exhaustedModel, workspace.Path, maxIterations: 2)
            .RunAsync("do it", TestContext.Current.CancellationToken);
        var completed = await CreateAgent(completedModel, workspace.Path)
            .RunAsync("do it", TestContext.Current.CancellationToken);

        Assert.Equal(2, exhausted.Iterations);
        Assert.Equal(2, completed.Iterations);
        Assert.Equal(AgentRunStatus.IterationLimitReached, exhausted.Status);
        Assert.Equal(AgentRunStatus.Completed, completed.Status);
        Assert.Equal(string.Empty, exhausted.AccumulatedText);
        Assert.Null(completed.AccumulatedText);
    }

    [Fact]
    public async Task ToolResultSecretsAreRedactedOnceAtIngestionInTheReplayedRequest()
    {
        using var workspace = new TemporaryDirectory();
        var model = new SequenceModelProvider([
            new ModelMessage(ModelRole.Assistant, null,
            [new ModelToolCall("call-1", "secret", "{}")]),
            new ModelMessage(ModelRole.Assistant, "finished")
        ]);
        var agent = new Agent(
            model,
            new ToolRegistry([new SecretOutputTool()]),
            new PolicyApprovalService(ApprovalMode.Workspace, static (_, _) => ValueTask.FromResult(false)),
            new StaticContextProvider("test context"),
            new SilentObserver(),
            new AgentOptions("fake-model"),
            workspace.Path);

        await agent.RunAsync("do it", TestContext.Current.CancellationToken);

        var toolMessage = Assert.Single(model.Requests[1].Messages, static m => m.Role == ModelRole.Tool);
        var content = toolMessage.Content!;
        Assert.Contains("API_KEY=[REDACTED]", content);
        Assert.Contains("OpenAI_API_KEY=[REDACTED]", content);
        Assert.Contains("export SECRET_TOKEN=[REDACTED]", content);
        Assert.DoesNotContain("sk-1111111111111111", content);
        Assert.DoesNotContain("hunter2", content);
        Assert.DoesNotContain("exported-secret", content);
        // Punctuation-bearing token must be redacted whole, not split at the dot.
        Assert.DoesNotContain(".leak", content);
        // Prefix-anchored non-match case: a filename like this must be left alone.
        Assert.Contains("ask-turn-default-auto.txt", content);
        Assert.Equal(1, model.Requests[1].Messages.Count(static m => m.Role == ModelRole.Tool));
    }

    [Fact]
    public async Task NewAgentContinuesAnExistingConversation()
    {
        using var workspace = new TemporaryDirectory();
        const string providerItems = """[{"type":"reasoning","id":"rs-1","encrypted_content":"opaque"}]""";
        var firstModel = new SequenceModelProvider([
            new ModelMessage(ModelRole.Assistant, "first answer", ProviderItemsJson: providerItems)
        ]);
        var first = await CreateAgent(firstModel, workspace.Path)
            .RunAsync("first question", TestContext.Current.CancellationToken);
        var secondModel = new SequenceModelProvider([
            new ModelMessage(ModelRole.Assistant, "second answer")
        ]);
        var secondAgent = new Agent(
            secondModel,
            new ToolRegistry([new EchoTool()]),
            new PolicyApprovalService(ApprovalMode.Workspace, static (_, _) => ValueTask.FromResult(false)),
            new StaticContextProvider("test context"),
            new SilentObserver(),
            new AgentOptions("other-model"),
            workspace.Path,
            first.Messages);

        var second = await secondAgent.RunAsync("second question", TestContext.Current.CancellationToken);

        var messages = second.Messages;
        Assert.Equal(5, messages.Count);
        Assert.Equal(ModelRole.System, messages[0].Role);
        Assert.Equal("first question", messages[1].Content);
        Assert.Equal("first answer", messages[2].Content);
        Assert.Equal(providerItems, messages[2].ProviderItemsJson);
        Assert.Equal("second question", messages[3].Content);
        Assert.Equal("second answer", messages[4].Content);
    }

    private static string Serialize(AgentEvent agentEvent)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            AgentEventJson.Write(writer, agentEvent);
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static Agent CreateAgent(
        IModelProvider model,
        string workspaceRoot,
        int maxIterations = 24,
        IAgentObserver? observer = null,
        ITool? tool = null,
        AgentOptions? options = null,
        AgentTurnMetadata? metadata = null) => new(
        model,
        new ToolRegistry([tool ?? new EchoTool()]),
        new PolicyApprovalService(ApprovalMode.Workspace, static (_, _) => ValueTask.FromResult(false)),
        new StaticContextProvider("test context"),
        observer ?? new SilentObserver(),
        options ?? new AgentOptions("fake-model", maxIterations),
        workspaceRoot,
        turnMetadata: metadata);

    private sealed class SequenceModelProvider(
        IReadOnlyList<ModelMessage> responses,
        ModelUsage? usage = null) : IModelProvider
    {
        private int _index;

        public List<ModelRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await Task.Yield();
            yield return new ModelCompleted(responses[_index++], usage);
        }
    }

    private sealed class ThrowingModelProvider(Exception exception) : IModelProvider
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw exception;
#pragma warning disable CS0162 // unreachable yield keeps this an iterator
            yield break;
#pragma warning restore CS0162
        }
    }

    private sealed class EchoTool : ITool
    {
        public ToolDefinition Definition { get; } = new(
            "echo",
            "Echo a value.",
            new System.Text.Json.Nodes.JsonObject { ["type"] = "object" });

        public int ExecutionCount { get; private set; }

        public ApprovalLevel Classify(JsonElement arguments) => ApprovalLevel.ReadOnly;

        public ValueTask<ToolResult> ExecuteAsync(
            JsonElement arguments,
            ToolContext context,
            CancellationToken cancellationToken = default)
        {
            ExecutionCount++;
            return ValueTask.FromResult(ToolResult.Ok("echo:" + arguments.GetProperty("value").GetString()));
        }
    }

    private sealed class MutatingTool : ITool
    {
        public ToolDefinition Definition { get; } = new(
            "mutate",
            "Mutate a value.",
            new System.Text.Json.Nodes.JsonObject { ["type"] = "object" });

        public int ExecutionCount { get; private set; }

        public ApprovalLevel Classify(JsonElement arguments) => ApprovalLevel.WorkspaceWrite;

        public ValueTask<ToolResult> ExecuteAsync(
            JsonElement arguments,
            ToolContext context,
            CancellationToken cancellationToken = default)
        {
            ExecutionCount++;
            return ValueTask.FromResult(ToolResult.Ok("mutated"));
        }
    }

    private sealed class SecretOutputTool : ITool
    {
        public ToolDefinition Definition { get; } = new(
            "secret",
            "Return secret-bearing output.",
            new System.Text.Json.Nodes.JsonObject { ["type"] = "object" });

        public ApprovalLevel Classify(JsonElement arguments) => ApprovalLevel.ReadOnly;

        public ValueTask<ToolResult> ExecuteAsync(
            JsonElement arguments,
            ToolContext context,
            CancellationToken cancellationToken = default)
        {
            const string output = """
                API_KEY=hunter2
                OpenAI_API_KEY=sk-1111111111111111
                export SECRET_TOKEN=exported-secret
                inline: sk-2222222222.leak
                file: ask-turn-default-auto.txt
                """;
            return ValueTask.FromResult(ToolResult.Ok(output));
        }
    }

    private sealed class InterruptingTool(CancellationTokenSource interruption) : ITool
    {
        public ToolDefinition Definition { get; } = new(
            "interrupt",
            "Cancel the turn while executing.",
            new System.Text.Json.Nodes.JsonObject { ["type"] = "object" });

        public ApprovalLevel Classify(JsonElement arguments) => ApprovalLevel.ReadOnly;

        public ValueTask<ToolResult> ExecuteAsync(
            JsonElement arguments,
            ToolContext context,
            CancellationToken cancellationToken = default)
        {
            interruption.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ToolResult.Ok("never reached"));
        }
    }

    private sealed class SilentObserver : IAgentObserver;

    private sealed class TypedRecordingObserver : IAgentObserver
    {
        public List<AgentEvent> Events { get; } = [];

        public ValueTask OnEventAsync(AgentEvent agentEvent, CancellationToken cancellationToken)
        {
            Events.Add(agentEvent);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestCompositeObserver(params IAgentObserver[] observers) : IAgentObserver
    {
        public async ValueTask OnEventAsync(AgentEvent agentEvent, CancellationToken cancellationToken)
        {
            foreach (var observer in observers)
            {
                await observer.OnEventAsync(agentEvent, cancellationToken);
            }
        }
    }

    private sealed class RecordingObserver : IAgentObserver
    {
        public List<(string Name, string ArgumentsJson, ApprovalLevel Level)> Started { get; } = [];

        public List<(string Name, string ArgumentsJson, string Reason)> Rejected { get; } = [];

        public List<EndpointIdentity> TurnStarts { get; } = [];

        public List<ModelMessage> Messages { get; } = [];

        public List<ModelUsage> Usage { get; } = [];

        public List<Exception> Errors { get; } = [];

        public int Interruptions { get; private set; }

        public ValueTask OnTurnStartedAsync(EndpointIdentity endpoint, CancellationToken cancellationToken)
        {
            TurnStarts.Add(endpoint);
            return ValueTask.CompletedTask;
        }

        public ValueTask OnMessageAsync(ModelMessage message, CancellationToken cancellationToken)
        {
            Messages.Add(message);
            return ValueTask.CompletedTask;
        }

        public ValueTask OnUsageAsync(ModelUsage usage, CancellationToken cancellationToken)
        {
            Usage.Add(usage);
            return ValueTask.CompletedTask;
        }

        public ValueTask OnTurnInterruptedAsync(CancellationToken cancellationToken)
        {
            Interruptions++;
            return ValueTask.CompletedTask;
        }

        public ValueTask OnTurnErrorAsync(Exception exception, CancellationToken cancellationToken)
        {
            Errors.Add(exception);
            return ValueTask.CompletedTask;
        }

        public ValueTask OnToolStartedAsync(
            string name,
            string argumentsJson,
            ApprovalLevel level,
            CancellationToken cancellationToken)
        {
            Started.Add((name, argumentsJson, level));
            return ValueTask.CompletedTask;
        }

        public ValueTask OnToolRejectedAsync(
            string name,
            string argumentsJson,
            string reason,
            CancellationToken cancellationToken)
        {
            Rejected.Add((name, argumentsJson, reason));
            return ValueTask.CompletedTask;
        }
    }
}
