using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using Wfx.Core;

namespace Wfx.Core.Tests;

public sealed class SessionPersistenceTests
{
    private static readonly Regex SessionIdPattern = new(
        @"^[0-9]{8}T[0-9]{6}Z-[a-z0-9]{6}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    [Fact]
    public async Task PersistsHeaderAndTurnEventsForACompletedTurn()
    {
        using var workspace = new TemporaryDirectory();
        using var sessions = new TemporaryDirectory();
        using var log = new SessionStore(sessions.Path).Create(workspace.Path);
        var model = new SequenceModelProvider(
            [new ModelMessage(ModelRole.Assistant, "finished")],
            usage: new ModelUsage(10, 4));
        var agent = CreateAgent(
            model,
            workspace.Path,
            new SessionRecorder(log),
            options: new AgentOptions(new EndpointIdentity("work", "openrouter", "responses", "fake-model")));

        await agent.RunAsync("do it", TestContext.Current.CancellationToken);

        Assert.Matches(SessionIdPattern, log.Id);
        Assert.Equal(Path.Combine(sessions.Path, log.Id + ".jsonl"), log.FilePath);

        var events = ReadEvents(log.FilePath);
        Assert.Equal("header", TypeOf(events[0]));
        Assert.Equal(1, events[0].GetProperty("schema_version").GetInt32());
        Assert.Equal(log.Id, events[0].GetProperty("session_id").GetString());
        Assert.Equal(Path.GetFullPath(workspace.Path), events[0].GetProperty("workspace").GetString());
        Assert.True(events[0].TryGetProperty("created_at", out var createdAt));
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$", createdAt.GetString());

        Assert.Equal("turn_started", TypeOf(events[1]));
        Assert.Equal("work", events[1].GetProperty("profile").GetString());
        Assert.Equal("openrouter", events[1].GetProperty("provider").GetString());
        Assert.Equal("responses", events[1].GetProperty("protocol").GetString());
        Assert.Equal("fake-model", events[1].GetProperty("model").GetString());

        Assert.Equal("message", TypeOf(events[2]));
        Assert.Equal("system", events[2].GetProperty("role").GetString());
        Assert.Contains("test context", events[2].GetProperty("content").GetString());

        Assert.Equal("message", TypeOf(events[3]));
        Assert.Equal("user", events[3].GetProperty("role").GetString());
        Assert.Equal("do it", events[3].GetProperty("content").GetString());

        Assert.Equal("message", TypeOf(events[4]));
        Assert.Equal("assistant", events[4].GetProperty("role").GetString());
        Assert.Equal("finished", events[4].GetProperty("content").GetString());

        Assert.Equal("usage", TypeOf(events[5]));
        Assert.Equal(10, events[5].GetProperty("input_tokens").GetInt64());
        Assert.Equal(4, events[5].GetProperty("output_tokens").GetInt64());
        Assert.Equal(6, events.Length);
    }

    [Fact]
    public async Task AppendsEventsDuringTheTurnNotAtTurnEnd()
    {
        using var workspace = new TemporaryDirectory();
        using var sessions = new TemporaryDirectory();
        using var log = new SessionStore(sessions.Path).Create(workspace.Path);
        var spy = new SessionSpyTool { SessionPath = log.FilePath };
        var model = new SequenceModelProvider([
            new ModelMessage(ModelRole.Assistant, "calling",
                [new ModelToolCall("call-1", "spy", "{}")]),
            new ModelMessage(ModelRole.Assistant, "finished")
        ], usage: new ModelUsage(2, 1));
        var agent = CreateAgent(model, workspace.Path, new SessionRecorder(log), spy);

        await agent.RunAsync("inspect", TestContext.Current.CancellationToken);

        Assert.NotEmpty(spy.LinesAtExecution);
        var during = ParseLines(spy.LinesAtExecution);
        Assert.Contains(during, static e => TypeOf(e) == "header");
        Assert.Contains(during, static e => TypeOf(e) == "turn_started");
        Assert.Contains(during, static e => TypeOf(e) == "message" && e.GetProperty("role").GetString() == "user");
        var assistant = Assert.Single(during, static e =>
            TypeOf(e) == "message" && e.GetProperty("role").GetString() == "assistant");
        Assert.Equal("calling", assistant.GetProperty("content").GetString());
        Assert.Equal("call-1", assistant.GetProperty("tool_calls")[0].GetProperty("id").GetString());
        Assert.Equal("spy", assistant.GetProperty("tool_calls")[0].GetProperty("name").GetString());
        Assert.Equal("{}", assistant.GetProperty("tool_calls")[0].GetProperty("arguments").GetString());
        Assert.Contains(during, static e => TypeOf(e) == "usage");
        Assert.DoesNotContain(during, static e =>
            TypeOf(e) == "message" && e.GetProperty("role").GetString() == "tool");
        Assert.DoesNotContain(during, static e => TypeOf(e) == "interrupted");
    }

    [Fact]
    public async Task RecordsProviderItemsAndToolResultsOnMessageEvents()
    {
        using var workspace = new TemporaryDirectory();
        using var sessions = new TemporaryDirectory();
        using var log = new SessionStore(sessions.Path).Create(workspace.Path);
        const string providerItems = """[{"type":"reasoning","id":"rs-1","encrypted_content":"opaque-blob"}]""";
        var model = new SequenceModelProvider([
            new ModelMessage(
                ModelRole.Assistant,
                "calling",
                [new ModelToolCall("call-1", "echo", "{\"value\":\"hello\"}")],
                ProviderItemsJson: providerItems),
            new ModelMessage(ModelRole.Assistant, "finished")
        ]);
        var agent = CreateAgent(model, workspace.Path, new SessionRecorder(log), new EchoTool());

        await agent.RunAsync("do it", TestContext.Current.CancellationToken);

        var events = ReadEvents(log.FilePath);
        var assistant = Assert.Single(events, static e =>
            TypeOf(e) == "message" &&
            e.GetProperty("role").GetString() == "assistant" &&
            e.GetProperty("content").GetString() == "calling");
        using var items = JsonDocument.Parse(providerItems);
        Assert.Equal(JsonValueKind.Array, assistant.GetProperty("provider_items").ValueKind);
        Assert.Equal(items.RootElement.GetRawText(), assistant.GetProperty("provider_items").GetRawText());

        var tool = Assert.Single(events, static e =>
            TypeOf(e) == "message" && e.GetProperty("role").GetString() == "tool");
        Assert.Equal("call-1", tool.GetProperty("tool_call_id").GetString());
        Assert.Equal("echo", tool.GetProperty("name").GetString());
        Assert.Contains("echo:hello", tool.GetProperty("content").GetString());
    }

    [Fact]
    public async Task RecordsUsagePerModelCall()
    {
        using var workspace = new TemporaryDirectory();
        using var sessions = new TemporaryDirectory();
        using var log = new SessionStore(sessions.Path).Create(workspace.Path);
        var model = new SequenceModelProvider([
            new ModelMessage(ModelRole.Assistant, null,
                [new ModelToolCall("call-1", "echo", "{\"value\":\"a\"}")]),
            new ModelMessage(ModelRole.Assistant, "finished")
        ], usage: new ModelUsage(10, 5));
        var agent = CreateAgent(model, workspace.Path, new SessionRecorder(log), new EchoTool());

        await agent.RunAsync("do it", TestContext.Current.CancellationToken);

        var usage = ReadEvents(log.FilePath).Where(static e => TypeOf(e) == "usage").ToArray();
        Assert.Equal(2, usage.Length);
        Assert.All(usage, static e =>
        {
            Assert.Equal(10, e.GetProperty("input_tokens").GetInt64());
            Assert.Equal(5, e.GetProperty("output_tokens").GetInt64());
        });
    }

    [Fact]
    public async Task RecordsEndpointIdentityPerTurnAndReflectsAModelSwitch()
    {
        using var workspace = new TemporaryDirectory();
        using var sessions = new TemporaryDirectory();
        using var log = new SessionStore(sessions.Path).Create(workspace.Path);
        var recorder = new SessionRecorder(log);
        var first = await CreateAgent(
            new SequenceModelProvider([new ModelMessage(ModelRole.Assistant, "first")]),
            workspace.Path,
            recorder,
            options: new AgentOptions(new EndpointIdentity("a", "openai", "chat_completions", "model-a")))
            .RunAsync("one", TestContext.Current.CancellationToken);
        await new Agent(
            new SequenceModelProvider([new ModelMessage(ModelRole.Assistant, "second")]),
            new ToolRegistry([new EchoTool()]),
            new PolicyApprovalService(ApprovalMode.Workspace, static (_, _) => ValueTask.FromResult(false)),
            new StaticContextProvider("test context"),
            recorder,
            new AgentOptions(new EndpointIdentity("b", "openrouter", "responses", "model-b")),
            workspace.Path,
            first.Messages).RunAsync("two", TestContext.Current.CancellationToken);

        var turns = ReadEvents(log.FilePath).Where(static e => TypeOf(e) == "turn_started").ToArray();
        Assert.Equal(2, turns.Length);
        Assert.Equal("a", turns[0].GetProperty("profile").GetString());
        Assert.Equal("openai", turns[0].GetProperty("provider").GetString());
        Assert.Equal("chat_completions", turns[0].GetProperty("protocol").GetString());
        Assert.Equal("model-a", turns[0].GetProperty("model").GetString());
        Assert.Equal("b", turns[1].GetProperty("profile").GetString());
        Assert.Equal("openrouter", turns[1].GetProperty("provider").GetString());
        Assert.Equal("responses", turns[1].GetProperty("protocol").GetString());
        Assert.Equal("model-b", turns[1].GetProperty("model").GetString());
        Assert.Single(ReadEvents(log.FilePath), static e => TypeOf(e) == "header");
    }

    [Fact]
    public async Task RecordsInterruptedAsAnEvent()
    {
        using var workspace = new TemporaryDirectory();
        using var sessions = new TemporaryDirectory();
        using var log = new SessionStore(sessions.Path).Create(workspace.Path);
        using var interruption = new CancellationTokenSource();
        var agent = CreateAgent(
            new SequenceModelProvider([
                new ModelMessage(ModelRole.Assistant, null,
                    [new ModelToolCall("call-1", "interrupt", "{}")])
            ]),
            workspace.Path,
            new SessionRecorder(log),
            new InterruptingTool(interruption));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => agent.RunAsync("do it", interruption.Token));

        var events = ReadEvents(log.FilePath);
        Assert.Contains(events, static e => TypeOf(e) == "interrupted");
        Assert.DoesNotContain(events, static e => TypeOf(e) == "error");
        var assistant = Assert.Single(events, static e =>
            TypeOf(e) == "message" && e.GetProperty("role").GetString() == "assistant");
        Assert.Equal("call-1", assistant.GetProperty("tool_calls")[0].GetProperty("id").GetString());
        Assert.Equal("interrupt", assistant.GetProperty("tool_calls")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task RecordsErrorAsAnEvent()
    {
        using var workspace = new TemporaryDirectory();
        using var sessions = new TemporaryDirectory();
        using var log = new SessionStore(sessions.Path).Create(workspace.Path);
        var agent = CreateAgent(
            new ThrowingModelProvider(new InvalidOperationException("provider failed")),
            workspace.Path,
            new SessionRecorder(log));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.RunAsync("do it", TestContext.Current.CancellationToken));

        var error = Assert.Single(ReadEvents(log.FilePath), static e => TypeOf(e) == "error");
        Assert.Equal("provider failed", error.GetProperty("message").GetString());
        Assert.DoesNotContain(ReadEvents(log.FilePath), static e => TypeOf(e) == "interrupted");
    }

    [Fact]
    public async Task RedactsKnownSecretShapesInErrorEvents()
    {
        using var workspace = new TemporaryDirectory();
        using var sessions = new TemporaryDirectory();
        using var log = new SessionStore(sessions.Path).Create(workspace.Path);
        var agent = CreateAgent(
            new ThrowingModelProvider(new InvalidOperationException("upstream rejected sk-1111111111111111")),
            workspace.Path,
            new SessionRecorder(log));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.RunAsync("do it", TestContext.Current.CancellationToken));

        var error = Assert.Single(ReadEvents(log.FilePath), static e => TypeOf(e) == "error");
        var message = error.GetProperty("message").GetString()!;
        Assert.DoesNotContain("sk-1111111111111111", message);
        Assert.Contains("[REDACTED]", message);
    }

    [Fact]
    public async Task PersistsRedactedToolOutputNotTheSecret()
    {
        using var workspace = new TemporaryDirectory();
        using var sessions = new TemporaryDirectory();
        using var log = new SessionStore(sessions.Path).Create(workspace.Path);
        var agent = CreateAgent(
            new SequenceModelProvider([
                new ModelMessage(ModelRole.Assistant, null,
                    [new ModelToolCall("call-1", "secret", "{}")]),
                new ModelMessage(ModelRole.Assistant, "finished")
            ]),
            workspace.Path,
            new SessionRecorder(log),
            new SecretOutputTool());

        await agent.RunAsync("do it", TestContext.Current.CancellationToken);

        var tool = Assert.Single(ReadEvents(log.FilePath), static e =>
            TypeOf(e) == "message" && e.GetProperty("role").GetString() == "tool");
        var content = tool.GetProperty("content").GetString()!;
        Assert.Contains("API_KEY=[REDACTED]", content);
        Assert.DoesNotContain("hunter2", content);
        Assert.Contains("ask-turn-default-auto.txt", content);
    }

    [Fact]
    public void SessionIdUsesUtcTimestampAndSixCharacterSuffix()
    {
        using var workspace = new TemporaryDirectory();
        using var sessions = new TemporaryDirectory();
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 8, 22, 15, 4, 5, TimeSpan.Zero));
        using var log = new SessionStore(sessions.Path, time).Create(workspace.Path);
        Assert.StartsWith("20260822T150405Z-", log.Id);
        Assert.Matches(SessionIdPattern, log.Id);
        Assert.Equal(Path.Combine(sessions.Path, log.Id + ".jsonl"), log.FilePath);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void CreatesSessionsDirectoryWithCurrentUserOnlyAcl()
    {
        using var sessions = new TemporaryDirectory();
        var root = Path.Combine(sessions.Path, "sessions");
        using var log = new SessionStore(root).Create(sessions.Path);
        log.Dispose();

        var security = new DirectoryInfo(root).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        var currentUser = WindowsIdentity.GetCurrent().User;
        Assert.NotNull(currentUser);
        Assert.Contains(rules, rule =>
            rule.IdentityReference == currentUser &&
            rule.AccessControlType == AccessControlType.Allow &&
            rule.FileSystemRights.HasFlag(FileSystemRights.FullControl));
        Assert.DoesNotContain(rules, static rule => rule.AccessControlType == AccessControlType.Allow
            && rule.IdentityReference != WindowsIdentity.GetCurrent().User);
    }

    private static Agent CreateAgent(
        IModelProvider model,
        string workspaceRoot,
        IAgentObserver observer,
        ITool? tool = null,
        AgentOptions? options = null) => new(
        model,
        new ToolRegistry([tool ?? new EchoTool()]),
        new PolicyApprovalService(ApprovalMode.Workspace, static (_, _) => ValueTask.FromResult(false)),
        new StaticContextProvider("test context"),
        observer,
        options ?? new AgentOptions("fake-model"),
        workspaceRoot);

    private static JsonElement[] ReadEvents(string path) => ParseLines(ReadLines(path));

    private static IReadOnlyList<string> ReadLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 0)
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    private static JsonElement[] ParseLines(IReadOnlyList<string> lines)
    {
        var events = new JsonElement[lines.Count];
        for (var i = 0; i < lines.Count; i++)
        {
            using var document = JsonDocument.Parse(lines[i]);
            events[i] = document.RootElement.Clone();
        }

        return events;
    }

    private static string TypeOf(JsonElement element) => element.GetProperty("type").GetString()!;

    private sealed class SequenceModelProvider(
        IReadOnlyList<ModelMessage> responses,
        ModelUsage? usage = null) : IModelProvider
    {
        private int _index;

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
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
#pragma warning disable CS0162
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

        public ApprovalLevel Classify(JsonElement arguments) => ApprovalLevel.ReadOnly;

        public ValueTask<ToolResult> ExecuteAsync(
            JsonElement arguments,
            ToolContext context,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ToolResult.Ok("echo:" + arguments.GetProperty("value").GetString()));
    }

    private sealed class SessionSpyTool : ITool
    {
        public List<string> LinesAtExecution { get; } = [];

        public required string SessionPath { get; init; }

        public ToolDefinition Definition { get; } = new(
            "spy",
            "Read the session file while executing.",
            new System.Text.Json.Nodes.JsonObject { ["type"] = "object" });

        public ApprovalLevel Classify(JsonElement arguments) => ApprovalLevel.ReadOnly;

        public ValueTask<ToolResult> ExecuteAsync(
            JsonElement arguments,
            ToolContext context,
            CancellationToken cancellationToken = default)
        {
            LinesAtExecution.AddRange(ReadLines(SessionPath));
            return ValueTask.FromResult(ToolResult.Ok("spied"));
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
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ToolResult.Ok("API_KEY=hunter2\nfile: ask-turn-default-auto.txt"));
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
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
}
