using System.Runtime.CompilerServices;
using System.Text.Json;
using Wfx.Core;

namespace Wfx.Cli.Tests;

[Collection("Console")]
public sealed class JsonEventStreamTests
{
    [Fact]
    public void JsonIsLimitedToTurnCommands()
    {
        Assert.True(CliArguments.Parse(["run", "--json", "prompt"]).Json);
        Assert.True(CliArguments.Parse(["resume", "--json"]).Json);

        foreach (var arguments in new[]
        {
            new[] { "--json" },
            ["sessions", "--json"],
            ["config", "--json"],
            ["models", "--json"],
            ["run", "--json", "--help"],
            ["resume", "--json", "--version"]
        })
        {
            var exception = Assert.Throws<ArgumentException>(() => CliArguments.Parse(arguments));
            Assert.Contains("wfx run --json", exception.Message);
            Assert.Contains("wfx resume --json", exception.Message);
        }
    }

    [Fact]
    public async Task RunJsonEmitsTurnStartedFirstAndACompletedTurn()
    {
        using var sessions = new TemporaryDirectory();
        using var httpClient = CliRunner.CreateUnexpectedHttpClient("The injected provider must be used.");
        using var console = new ConsoleCapture();
        var store = new SessionStore(sessions.Path);
        var provider = new SequenceModelProvider([
            new ModelCompleted(new ModelMessage(ModelRole.Assistant, "finished"), new ModelUsage(10, 4))
        ]);

        var exitCode = await CliRunner.RunAsync(
            [
                "run", "--json", "--approval", "never", "--provider", "local",
                "--base-url", "https://example.test/v1", "--model", "fake-model", "do it"
            ],
            httpClient,
            store,
            TestContext.Current.CancellationToken,
            modelProviderFactory: (_, _) => provider);

        Assert.Equal(0, exitCode);
        var events = ParseLines(console.Output.ToString());
        var started = events[0];
        Assert.Equal("turn_started", started.GetProperty("event").GetString());
        Assert.Equal(1, started.GetProperty("schema_version").GetInt32());
        Assert.Equal(Assert.Single(store.List().Sessions).SessionId, started.GetProperty("session_id").GetString());
        Assert.Equal("never", started.GetProperty("approval_mode").GetString());
        Assert.Equal("local", started.GetProperty("endpoint").GetProperty("provider").GetString());
        Assert.Equal("turn_completed", events[^1].GetProperty("event").GetString());
        Assert.Equal("finished", events[^1].GetProperty("final_message").GetString());
        Assert.Equal(14, events[^1].GetProperty("total_usage").GetProperty("total_tokens").GetInt64());
    }

    [Fact]
    public async Task ToolEventsPreserveOrderCallIdsAndRawArguments()
    {
        using var sessions = new TemporaryDirectory();
        using var httpClient = CliRunner.CreateUnexpectedHttpClient("The injected provider must be used.");
        using var console = new ConsoleCapture();
        var provider = new QueuedModelProvider([
            new ModelCompleted(new ModelMessage(
                ModelRole.Assistant,
                null,
                [new ModelToolCall("call-rejected", "missing_tool", "{not-json")]
            )),
            new ModelCompleted(new ModelMessage(
                ModelRole.Assistant,
                null,
                [new ModelToolCall("call-completed", "list_directory", "{\"path\":\".\"}")]
            )),
            new ModelCompleted(new ModelMessage(ModelRole.Assistant, "done"))
        ]);

        var exitCode = await CliRunner.RunAsync(
            [
                "run", "--json", "--approval", "never", "--provider", "local",
                "--base-url", "https://example.test/v1", "--model", "fake-model", "inspect"
            ],
            httpClient,
            new SessionStore(sessions.Path),
            TestContext.Current.CancellationToken,
            modelProviderFactory: (_, _) => provider);

        Assert.Equal(0, exitCode);
        var toolEvents = ParseLines(console.Output.ToString())
            .Where(static item => item.GetProperty("event").GetString()!.StartsWith("tool_", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(["tool_rejected", "tool_started", "tool_completed"],
            toolEvents.Select(static item => item.GetProperty("event").GetString()));
        Assert.Equal("call-rejected", toolEvents[0].GetProperty("call_id").GetString());
        Assert.Equal("{not-json", toolEvents[0].GetProperty("arguments_json").GetString());
        Assert.Equal(JsonValueKind.String, toolEvents[0].GetProperty("arguments_json").ValueKind);
        Assert.Equal("call-completed", toolEvents[1].GetProperty("call_id").GetString());
        Assert.Equal("call-completed", toolEvents[2].GetProperty("call_id").GetString());
        Assert.False(toolEvents[2].GetProperty("result").GetProperty("is_error").GetBoolean());
    }

    [Fact]
    public async Task ResumeJsonStreamsOnePromptUnderTheExistingSessionId()
    {
        using var sessions = new TemporaryDirectory();
        using var httpClient = CliRunner.CreateUnexpectedHttpClient("The injected provider must be used.");
        using var console = new ConsoleCapture("continue\n");
        var store = new SessionStore(sessions.Path);
        var session = store.Create(WorkspaceInfo.Discover().Root);
        var sessionId = session.Id;
        await new SessionRecorder(session).OnTurnStartedAsync(
            new EndpointIdentity(null, "local", "chat_completions", "fake-model"),
            TestContext.Current.CancellationToken);
        session.Dispose();
        var provider = new SequenceModelProvider([
            new ModelCompleted(new ModelMessage(ModelRole.Assistant, "resumed"))
        ]);

        var exitCode = await CliRunner.RunAsync(
            [
                "resume", "--id", sessionId, "--json", "--approval", "never", "--provider", "local",
                "--base-url", "https://example.test/v1", "--model", "fake-model"
            ],
            httpClient,
            store,
            TestContext.Current.CancellationToken,
            modelProviderFactory: (_, _) => provider);

        Assert.Equal(0, exitCode);
        var events = ParseLines(console.Output.ToString());
        Assert.Equal("turn_started", events[0].GetProperty("event").GetString());
        Assert.Equal(sessionId, events[0].GetProperty("session_id").GetString());
        Assert.Equal("resumed", events[^1].GetProperty("final_message").GetString());
    }

    [Fact]
    public async Task MaxIterationsJsonReturnsFourAndEmitsInterruption()
    {
        using var sessions = new TemporaryDirectory();
        using var httpClient = CliRunner.CreateUnexpectedHttpClient("The injected provider must be used.");
        using var console = new ConsoleCapture();
        var provider = new QueuedModelProvider([
            new ModelCompleted(new ModelMessage(
                ModelRole.Assistant,
                "still working",
                [new ModelToolCall("call-1", "list_directory", "{\"path\":\".\"}")]))
        ]);

        var exitCode = await CliRunner.RunAsync(
            [
                "run", "--json", "--approval", "never", "--provider", "local",
                "--base-url", "https://example.test/v1", "--model", "fake-model",
                "--max-iterations", "1", "inspect"
            ],
            httpClient,
            new SessionStore(sessions.Path),
            TestContext.Current.CancellationToken,
            modelProviderFactory: (_, _) => provider);

        Assert.Equal(4, exitCode);
        var interrupted = ParseLines(console.Output.ToString())[^1];
        Assert.Equal("turn_interrupted", interrupted.GetProperty("event").GetString());
        Assert.Equal("max_iterations", interrupted.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task ProviderErrorJsonReturnsFiveAndEmitsClassifiedError()
    {
        using var sessions = new TemporaryDirectory();
        using var httpClient = CliRunner.CreateUnexpectedHttpClient("The injected provider must be used.");
        using var console = new ConsoleCapture();

        var exitCode = await CliRunner.RunAsync(
            [
                "run", "--json", "--approval", "never", "--provider", "local",
                "--base-url", "https://example.test/v1", "--model", "fake-model", "fail"
            ],
            httpClient,
            new SessionStore(sessions.Path),
            TestContext.Current.CancellationToken,
            modelProviderFactory: (_, _) => new ThrowingModelProvider());

        Assert.Equal(5, exitCode);
        var error = ParseLines(console.Output.ToString())[^1];
        Assert.Equal("turn_error", error.GetProperty("event").GetString());
        Assert.Equal("provider_error", error.GetProperty("error").GetProperty("kind").GetString());
        Assert.Equal("provider failed", error.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task JsonOutsideTurnCommandsReturnsUsageError()
    {
        foreach (var arguments in new[]
        {
            new[] { "--json" },
            ["sessions", "--json"],
            ["config", "--json"],
            ["models", "--json"],
            ["run", "--json", "--help"],
            ["resume", "--json", "--version"]
        })
        {
            using var httpClient = CliRunner.CreateUnexpectedHttpClient("A usage error must not call a model endpoint.");
            using var console = new ConsoleCapture();
            var exitCode = await CliRunner.RunAsync(
                arguments,
                httpClient,
                new TestSessionStore(),
                TestContext.Current.CancellationToken);

            Assert.Equal(1, exitCode);
            Assert.Contains("wfx run --json", console.ErrorText);
            Assert.Contains("wfx resume --json", console.ErrorText);
        }
    }

    [Fact]
    public async Task CancelledJsonTurnReturnsFourAndEmitsInterruption()
    {
        using var sessions = new TemporaryDirectory();
        using var httpClient = CliRunner.CreateUnexpectedHttpClient("The injected provider must be used.");
        using var console = new ConsoleCapture();
        using var cancellation = new CancellationTokenSource();
        var provider = new SelfCancellingModelProvider(cancellation);

        var exitCode = await CliRunner.RunAsync(
            [
                "run", "--json", "--approval", "never", "--provider", "local",
                "--base-url", "https://example.test/v1", "--model", "fake-model", "stop"
            ],
            httpClient,
            new SessionStore(sessions.Path),
            cancellation.Token,
            modelProviderFactory: (_, _) => provider);

        Assert.Equal(4, exitCode);
        var interrupted = ParseLines(console.Output.ToString())[^1];
        Assert.Equal("turn_interrupted", interrupted.GetProperty("event").GetString());
        Assert.Equal("cancelled", interrupted.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task HelpDocumentsJsonFlagCredentialWarningAndTurnExitCodes()
    {
        using var httpClient = CliRunner.CreateUnexpectedHttpClient("Help must not call a model endpoint.");
        using var console = new ConsoleCapture();

        var exitCode = await CliRunner.RunAsync(
            ["--help"],
            httpClient,
            new TestSessionStore(),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        var help = console.Output.ToString();
        Assert.Contains("--json", help);
        Assert.Contains("credential-adjacent", help);
        Assert.Contains("4    JSON turn interrupted", help);
        Assert.Contains("5    JSON turn error", help);
    }

    [Fact]
    public async Task ObserverFlushesAfterEveryEvent()
    {
        using var output = new FlushTrackingWriter();
        var observer = new NdjsonAgentObserver(output);

        await observer.OnEventAsync(
            new MessageEvent(new ModelMessage(ModelRole.User, "one"), DateTimeOffset.UnixEpoch),
            TestContext.Current.CancellationToken);
        await observer.OnEventAsync(
            new MessageEvent(new ModelMessage(ModelRole.Assistant, "two"), DateTimeOffset.UnixEpoch),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, output.FlushCount);
        Assert.Equal(2, ParseLines(output.ToString()).Length);
    }

    [Fact]
    public async Task CanonicalEventsValidateAgainstPublishedSchema()
    {
        using var output = new StringWriter();
        var observer = new NdjsonAgentObserver(output);
        var at = new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);
        var events = new AgentEvent[]
        {
            new TurnStartedEvent(
                "20260824T120000Z-abc123",
                @"C:\workspace",
                new EndpointIdentity("work", "local", "chat_completions", "model"),
                ApprovalMode.Never,
                at),
            new MessageEvent(
                new ModelMessage(
                    ModelRole.Assistant,
                    "calling",
                    [new ModelToolCall("call-1", "list_directory", "{\"path\":\".\"}")],
                    ProviderItemsJson: "[{\"type\":\"reasoning\"}]"),
                at),
            new ToolStartedEvent("call-1", "list_directory", "{\"path\":\".\"}", ApprovalLevel.ReadOnly, at),
            new ToolCompletedEvent("call-1", "list_directory", ToolResult.Ok("README.md"), TimeSpan.FromMilliseconds(12), at),
            new ToolRejectedEvent("call-2", "write_file", "{bad", "denied", at),
            new UsageEvent(new ModelUsage(10, 4), at),
            new TurnCompletedEvent("20260824T120000Z-abc123", 2, "done", new ModelUsage(20, 8), at),
            new TurnInterruptedEvent("20260824T120000Z-abc123", AgentInterruptionReason.Timeout, at),
            new TurnErrorEvent(
                "20260824T120000Z-abc123",
                new AgentError(AgentErrorKind.ProviderError, "failed"),
                at)
        };
        foreach (var agentEvent in events)
        {
            await observer.OnEventAsync(agentEvent, TestContext.Current.CancellationToken);
        }

        using var schemaDocument = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "docs", "schemas", "wfx-events.v1.json"),
            TestContext.Current.CancellationToken));
        var schema = schemaDocument.RootElement;
        var definitions = schema.GetProperty("$defs");
        foreach (var instance in ParseLines(output.ToString()))
        {
            var eventName = instance.GetProperty("event").GetString()!;
            var errors = new List<string>();
            Validate(instance, definitions.GetProperty(eventName), schema, "$", errors);
            Assert.True(errors.Count == 0, $"{eventName}: {string.Join("; ", errors)}");
        }

        foreach (var definition in definitions.EnumerateObject())
        {
            AssertVisibility(definition.Value, $"#/$defs/{definition.Name}");
        }
    }

    private static void Validate(
        JsonElement instance,
        JsonElement schema,
        JsonElement rootSchema,
        string path,
        List<string> errors)
    {
        if (schema.TryGetProperty("$ref", out var reference))
        {
            var target = rootSchema;
            foreach (var segment in reference.GetString()![2..].Split('/'))
            {
                target = target.GetProperty(segment);
            }

            Validate(instance, target, rootSchema, path, errors);
            return;
        }

        if (schema.TryGetProperty("const", out var constant) && !JsonElement.DeepEquals(instance, constant))
        {
            errors.Add($"{path} does not match const {constant.GetRawText()}");
        }

        if (schema.TryGetProperty("enum", out var choices) &&
            !choices.EnumerateArray().Any(choice => JsonElement.DeepEquals(instance, choice)))
        {
            errors.Add($"{path} is not in enum {choices.GetRawText()}");
        }

        if (schema.TryGetProperty("type", out var type) && !MatchesType(instance, type))
        {
            errors.Add($"{path} has type {instance.ValueKind}, expected {type.GetRawText()}");
            return;
        }

        if (instance.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var required))
            {
                foreach (var name in required.EnumerateArray().Select(static item => item.GetString()!))
                {
                    if (!instance.TryGetProperty(name, out _))
                    {
                        errors.Add($"{path} is missing {name}");
                    }
                }
            }

            if (schema.TryGetProperty("properties", out var properties))
            {
                foreach (var property in properties.EnumerateObject())
                {
                    if (instance.TryGetProperty(property.Name, out var value))
                    {
                        Validate(value, property.Value, rootSchema, $"{path}.{property.Name}", errors);
                    }
                }
            }
        }

        if (instance.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
        {
            var index = 0;
            foreach (var item in instance.EnumerateArray())
            {
                Validate(item, items, rootSchema, $"{path}[{index++}]", errors);
            }
        }
    }

    private static bool MatchesType(JsonElement instance, JsonElement type) => type.ValueKind switch
    {
        JsonValueKind.String => MatchesType(instance, type.GetString()!),
        JsonValueKind.Array => type.EnumerateArray().Any(item => MatchesType(instance, item.GetString()!)),
        _ => false
    };

    private static bool MatchesType(JsonElement instance, string type) => type switch
    {
        "object" => instance.ValueKind == JsonValueKind.Object,
        "array" => instance.ValueKind == JsonValueKind.Array,
        "string" => instance.ValueKind == JsonValueKind.String,
        "integer" => instance.ValueKind == JsonValueKind.Number && instance.TryGetInt64(out _),
        "number" => instance.ValueKind == JsonValueKind.Number,
        "boolean" => instance.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => instance.ValueKind == JsonValueKind.Null,
        _ => false
    };

    private static void AssertVisibility(JsonElement schema, string path)
    {
        if (schema.TryGetProperty("properties", out var properties))
        {
            foreach (var property in properties.EnumerateObject())
            {
                Assert.True(
                    property.Value.TryGetProperty("x-wfx-visibility", out var visibility) &&
                    visibility.GetString() is "public" or "internal",
                    $"{path}/properties/{property.Name} must be marked public or internal.");
                AssertVisibility(property.Value, $"{path}/properties/{property.Name}");
            }
        }

        if (schema.TryGetProperty("items", out var items))
        {
            AssertVisibility(items, $"{path}/items");
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

    private sealed class SequenceModelProvider(IReadOnlyList<ModelStreamEvent> events) : IModelProvider
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (var modelEvent in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return modelEvent;
            }
        }
    }

    private sealed class QueuedModelProvider(IReadOnlyList<ModelCompleted> responses) : IModelProvider
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

    private sealed class SelfCancellingModelProvider(CancellationTokenSource source) : IModelProvider
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            source.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            yield break;
        }
    }

    private sealed class ThrowingModelProvider : IModelProvider
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw new HttpRequestException("provider failed");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    private sealed class FlushTrackingWriter : StringWriter
    {
        public int FlushCount { get; private set; }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FlushCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = Directory.CreateTempSubdirectory("wfx-json-events-").FullName;
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
