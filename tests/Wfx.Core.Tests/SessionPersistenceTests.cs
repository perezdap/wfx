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
        using var session = new SessionFixture();
        var model = new SequenceModelProvider(
            [new ModelMessage(ModelRole.Assistant, "finished")],
            usage: new ModelUsage(10, 4));
        var agent = CreateAgent(
            model,
            session,
            options: new AgentOptions(new EndpointIdentity("work", "openrouter", "responses", "fake-model")));

        await agent.RunAsync("do it", TestContext.Current.CancellationToken);

        Assert.Matches(SessionIdPattern, session.Log.Id);
        Assert.Equal(Path.Combine(session.SessionsPath, session.Log.Id + ".jsonl"), session.Log.FilePath);

        var events = session.Events();
        Assert.Equal("header", TypeOf(events[0]));
        Assert.Equal(1, events[0].GetProperty("schema_version").GetInt32());
        Assert.Equal(session.Log.Id, events[0].GetProperty("session_id").GetString());
        Assert.Equal(Path.GetFullPath(session.Workspace), events[0].GetProperty("workspace").GetString());
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
        using var session = new SessionFixture();
        var spy = new SessionSpyTool { SessionPath = session.Log.FilePath };
        var model = new SequenceModelProvider([
            new ModelMessage(ModelRole.Assistant, "calling",
                [new ModelToolCall("call-1", "spy", "{}")]),
            new ModelMessage(ModelRole.Assistant, "finished")
        ], usage: new ModelUsage(2, 1));
        var agent = CreateAgent(model, session, spy);

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
        using var session = new SessionFixture();
        const string providerItems = """[ { "type": "reasoning", "id": "rs-1", "encrypted_content": "opaque-blob" } ]""";
        var model = new SequenceModelProvider([
            new ModelMessage(
                ModelRole.Assistant,
                "calling",
                [new ModelToolCall("call-1", "echo", "{\"value\":\"hello\"}")],
                ProviderItemsJson: providerItems),
            new ModelMessage(ModelRole.Assistant, "finished")
        ]);
        var agent = CreateAgent(model, session, new EchoTool());

        await agent.RunAsync("do it", TestContext.Current.CancellationToken);

        var events = session.Events();
        var assistant = Assert.Single(events, static e =>
            TypeOf(e) == "message" &&
            e.GetProperty("role").GetString() == "assistant" &&
            e.GetProperty("content").GetString() == "calling");
        Assert.Equal(JsonValueKind.Array, assistant.GetProperty("provider_items").ValueKind);
        Assert.Equal(providerItems, assistant.GetProperty("provider_items").GetRawText());

        var tool = Assert.Single(events, static e =>
            TypeOf(e) == "message" && e.GetProperty("role").GetString() == "tool");
        Assert.Equal("call-1", tool.GetProperty("tool_call_id").GetString());
        Assert.Equal("echo", tool.GetProperty("name").GetString());
        Assert.Contains("echo:hello", tool.GetProperty("content").GetString());
    }

    [Fact]
    public async Task ReadsTranscriptAndReplaysProviderItemsAtTheAgentSeam()
    {
        using var session = new SessionFixture();
        const string providerItems = """[ { "type": "reasoning", "id": "rs-1", "encrypted_content": "opaque-blob" } ]""";
        var first = CreateAgent(
            new SequenceModelProvider([
                new ModelMessage(
                    ModelRole.Assistant,
                    "first",
                    [new ModelToolCall("call-1", "echo", "{\"value\":\"hello\"}")],
                    ProviderItemsJson: providerItems),
                new ModelMessage(ModelRole.Assistant, "completed")
            ]),
            session,
            new EchoTool(),
            new AgentOptions(new EndpointIdentity("work", "openrouter", "responses", "fake-model")));
        await first.RunAsync("one", TestContext.Current.CancellationToken);

        var transcript = new SessionStore(session.SessionsPath).Read(session.Log.Id);
        Assert.Equal(Path.GetFullPath(session.Workspace), transcript.Workspace);
        Assert.Equal(new EndpointIdentity("work", "openrouter", "responses", "fake-model"), transcript.LastEndpoint);
        Assert.Equal(5, transcript.Messages.Count);
        Assert.Equal(providerItems, transcript.Messages[2].ProviderItemsJson);

        var capture = new CapturingModelProvider(new ModelMessage(ModelRole.Assistant, "second"));
        var resumed = CreateAgent(
            capture,
            session.Log,
            session.Workspace,
            transcript.Messages,
            new AgentOptions(transcript.LastEndpoint!));
        await resumed.RunAsync("two", TestContext.Current.CancellationToken);

        Assert.NotNull(capture.LastRequest);
        Assert.Equal(
            transcript.Messages
                .Append(new ModelMessage(ModelRole.User, "two"))
                .Append(new ModelMessage(ModelRole.Assistant, "second")),
            capture.LastRequest!.Messages);
        Assert.Equal(providerItems, capture.LastRequest.Messages[2].ProviderItemsJson);
        Assert.Equal(1, capture.LastRequest.Messages.Count(static message => message.Role == ModelRole.System));
    }

    [Fact]
    public async Task OpensExistingSessionForAppendWithoutWritingAnotherHeader()
    {
        using var session = new SessionFixture();
        await CreateAgent(
            new SequenceModelProvider([new ModelMessage(ModelRole.Assistant, "first")]),
            session).RunAsync("one", TestContext.Current.CancellationToken);
        session.Log.Dispose();

        var store = new SessionStore(session.SessionsPath);
        var beforeOpen = File.ReadAllBytes(session.Log.FilePath);
        using (store.Open(session.Log.Id))
        {
        }

        Assert.Equal(beforeOpen, File.ReadAllBytes(session.Log.FilePath));
        using var reopened = store.Open(session.Log.Id);
        var transcript = store.Read(session.Log.Id);
        await CreateAgent(
            new SequenceModelProvider([new ModelMessage(ModelRole.Assistant, "second")]),
            reopened,
            session.Workspace,
            transcript.Messages,
            new AgentOptions(transcript.LastEndpoint!))
            .RunAsync("two", TestContext.Current.CancellationToken);

        var events = session.Events();
        Assert.Single(events, static e => TypeOf(e) == "header");
        Assert.Equal(2, events.Count(static e => TypeOf(e) == "turn_started"));
        Assert.Contains(events, static e => TypeOf(e) == "message" && e.GetProperty("content").GetString() == "one");
        Assert.Contains(events, static e => TypeOf(e) == "message" && e.GetProperty("content").GetString() == "two");
    }

    [Fact]
    public async Task OpensSessionByDiscardingAnUnterminatedTailBeforeAppending()
    {
        using var workspace = new TemporaryDirectory();
        using var sessions = new TemporaryDirectory();
        var store = new SessionStore(sessions.Path);
        var log = store.Create(workspace.Path);
        var sessionId = log.Id;
        var sessionPath = log.FilePath;
        log.Dispose();

        var durablePrefix = Header(sessionId, Path.GetFullPath(workspace.Path)) + "\r\n";
        var partial = """{"type":"message","role":"user","content":""" + new string('x', 70_000);
        File.WriteAllText(
            sessionPath,
            durablePrefix + """{"type":"message","role":"user","content":"kept"}""" + "\r\n" + partial);

        using var reopened = store.Open(sessionId);
        await new SessionRecorder(reopened).OnMessageAsync(
            new ModelMessage(ModelRole.User, "appended"),
            TestContext.Current.CancellationToken);

        var transcript = store.Read(sessionId);
        Assert.Equal(["kept", "appended"], transcript.Messages.Select(static message => message.Content));
        reopened.Dispose();
        Assert.StartsWith(
            durablePrefix + """{"type":"message","role":"user","content":"kept"}""" + "\r\n",
            File.ReadAllText(sessionPath),
            StringComparison.Ordinal);
    }

    [Fact]
    public void OpenRefusesASessionFileWithNoCompleteLine()
    {
        using var workspace = new TemporaryDirectory();
        using var sessions = new TemporaryDirectory();
        var store = new SessionStore(sessions.Path);
        const string sessionId = "20260822T150405Z-no-newline";
        var path = Path.Combine(sessions.Path, sessionId + ".jsonl");
        var content = Header(sessionId, workspace.Path);
        File.WriteAllText(path, content);

        var exception = Assert.Throws<IOException>(() => store.Open(sessionId));

        Assert.Contains("no complete newline-terminated event", exception.Message);
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Fact]
    public void FindsLatestSessionOnlyForTheRequestedWorkspace()
    {
        using var sessions = new TemporaryDirectory();
        using var firstWorkspace = new TemporaryDirectory();
        using var secondWorkspace = new TemporaryDirectory();
        var store = new SessionStore(sessions.Path);
        var first = store.Create(firstWorkspace.Path);
        first.Dispose();
        var second = store.Create(secondWorkspace.Path);
        second.Dispose();
        var latestFirst = store.Create(firstWorkspace.Path);
        latestFirst.Dispose();

        File.SetLastWriteTimeUtc(
            Path.Combine(sessions.Path, first.Id + ".jsonl"),
            DateTime.UtcNow.AddMinutes(-3));
        File.SetLastWriteTimeUtc(
            Path.Combine(sessions.Path, second.Id + ".jsonl"),
            DateTime.UtcNow.AddMinutes(10));
        File.SetLastWriteTimeUtc(
            Path.Combine(sessions.Path, latestFirst.Id + ".jsonl"),
            DateTime.UtcNow.AddMinutes(-1));
        File.WriteAllText(
            Path.Combine(sessions.Path, "20260822T150405Z-corrupt-workspace.jsonl"),
            Header("20260822T150405Z-corrupt-workspace", "C:\\bad*workspace") + "\n");

        Assert.Equal(latestFirst.Id, store.FindLatest(firstWorkspace.Path)?.SessionId);
        Assert.Equal(second.Id, store.FindLatest(secondWorkspace.Path)?.SessionId);
        Assert.Null(store.FindLatest(Path.Combine(sessions.Path, "missing-workspace")));
    }

    [Fact]
    public void ReadAndOpenRefuseSessionIdsThatCouldEscapeTheStoreDirectory()
    {
        using var sessions = new TemporaryDirectory();
        var store = new SessionStore(sessions.Path);
        var ids = new[]
        {
            "../outside",
            "..\\outside",
            "nested/session",
            Path.GetFullPath("absolute-session")
        };

        foreach (var id in ids)
        {
            Assert.Throws<FileNotFoundException>(() => store.Read(id));
            Assert.Throws<FileNotFoundException>(() => store.Open(id));
        }
    }

    [Fact]
    public void ReaderSkipsUnknownEventsAtSupportedSchemaVersions()
    {
        using var sessions = new TemporaryDirectory();
        using var workspace = new TemporaryDirectory();
        var store = new SessionStore(sessions.Path);
        const string id = "20260822T150405Z-reader1";
        var path = Path.Combine(sessions.Path, id + ".jsonl");
        File.WriteAllText(
            path,
            Header(id, workspace.Path) + "\n" +
            """{"type":"future_event","value":"ignored"}""" + "\n" +
            """{"type":"message","role":"user","content":"kept"}""" + "\n");

        var transcript = store.Read(id);
        Assert.Single(transcript.Messages);
        Assert.Equal("kept", transcript.Messages[0].Content);
    }

    [Fact]
    public void ReaderSkipsWhitespaceOnlyLinesBetweenEvents()
    {
        using var sessions = new TemporaryDirectory();
        using var workspace = new TemporaryDirectory();
        var store = new SessionStore(sessions.Path);
        const string id = "20260822T150405Z-whitespace";
        var path = Path.Combine(sessions.Path, id + ".jsonl");
        File.WriteAllText(
            path,
            Header(id, workspace.Path) + "\n" +
            """{"type":"message","role":"user","content":"first"}""" + "\n" +
            "  \t  \n" +
            """{"type":"message","role":"user","content":"second"}""" + "\n");

        var transcript = store.Read(id);
        Assert.Equal(["first", "second"], transcript.Messages.Select(static message => message.Content));
    }

    [Fact]
    public void ReaderDiscardsAnUnterminatedFinalEvent()
    {
        using var sessions = new TemporaryDirectory();
        using var workspace = new TemporaryDirectory();
        var store = new SessionStore(sessions.Path);
        const string id = "20260822T150405Z-truncated";
        var path = Path.Combine(sessions.Path, id + ".jsonl");
        File.WriteAllText(
            path,
            Header(id, workspace.Path) + "\n" +
            """{"type":"message","role":"user","content":"kept"}""" + "\n" +
            """{"type":"message","role":"user","content":"truncated"}""");

        var transcript = store.Read(id);
        Assert.Single(transcript.Messages);
        Assert.Equal("kept", transcript.Messages[0].Content);
    }

    [Fact]
    public void ReaderRefusesANewerSchemaVersion()
    {
        using var sessions = new TemporaryDirectory();
        using var workspace = new TemporaryDirectory();
        var store = new SessionStore(sessions.Path);
        const string id = "20260822T150405Z-newer1";
        var path = Path.Combine(sessions.Path, id + ".jsonl");
        File.WriteAllText(path, Header(id, workspace.Path, schemaVersion: 2) + "\n");

        var exception = Assert.Throws<InvalidDataException>(() => store.Read(id));
        Assert.Contains("schema_version 2", exception.Message);
        Assert.Contains("supports version 1", exception.Message);
    }

    [Fact]
    public void ReaderRejectsMalformedMidFileJson()
    {
        using var sessions = new TemporaryDirectory();
        using var workspace = new TemporaryDirectory();
        var store = new SessionStore(sessions.Path);
        const string id = "20260822T150405Z-corrupt1";
        var path = Path.Combine(sessions.Path, id + ".jsonl");
        File.WriteAllText(
            path,
            Header(id, workspace.Path) + "\n{not-json\n" +
            """{"type":"message","role":"user","content":"later"}""" + "\n");

        var exception = Assert.Throws<InvalidDataException>(() => store.Read(id));
        Assert.Contains("line 2", exception.Message);
    }

    [Fact]
    public async Task RecordsUsagePerModelCall()
    {
        using var session = new SessionFixture();
        var model = new SequenceModelProvider([
            new ModelMessage(ModelRole.Assistant, null,
                [new ModelToolCall("call-1", "echo", "{\"value\":\"a\"}")]),
            new ModelMessage(ModelRole.Assistant, "finished")
        ], usage: new ModelUsage(10, 5));
        var agent = CreateAgent(model, session, new EchoTool());

        await agent.RunAsync("do it", TestContext.Current.CancellationToken);

        var usage = session.Events().Where(static e => TypeOf(e) == "usage").ToArray();
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
        using var session = new SessionFixture();
        var first = await CreateAgent(
            new SequenceModelProvider([new ModelMessage(ModelRole.Assistant, "first")]),
            session,
            options: new AgentOptions(new EndpointIdentity("a", "openai", "chat_completions", "model-a")))
            .RunAsync("one", TestContext.Current.CancellationToken);
        await new Agent(
            new SequenceModelProvider([new ModelMessage(ModelRole.Assistant, "second")]),
            new ToolRegistry([new EchoTool()]),
            new PolicyApprovalService(ApprovalMode.Workspace, static (_, _) => ValueTask.FromResult(false)),
            new StaticContextProvider("test context"),
            session.Recorder,
            new AgentOptions(new EndpointIdentity("b", "openrouter", "responses", "model-b")),
            session.Workspace,
            first.Messages).RunAsync("two", TestContext.Current.CancellationToken);

        var turns = session.Events().Where(static e => TypeOf(e) == "turn_started").ToArray();
        Assert.Equal(2, turns.Length);
        Assert.Equal("a", turns[0].GetProperty("profile").GetString());
        Assert.Equal("openai", turns[0].GetProperty("provider").GetString());
        Assert.Equal("chat_completions", turns[0].GetProperty("protocol").GetString());
        Assert.Equal("model-a", turns[0].GetProperty("model").GetString());
        Assert.Equal("b", turns[1].GetProperty("profile").GetString());
        Assert.Equal("openrouter", turns[1].GetProperty("provider").GetString());
        Assert.Equal("responses", turns[1].GetProperty("protocol").GetString());
        Assert.Equal("model-b", turns[1].GetProperty("model").GetString());
        Assert.Single(session.Events(), static e => TypeOf(e) == "header");
    }

    [Fact]
    public async Task RecordsInterruptedAsAnEvent()
    {
        using var session = new SessionFixture();
        using var interruption = new CancellationTokenSource();
        var agent = CreateAgent(
            new SequenceModelProvider([
                new ModelMessage(ModelRole.Assistant, null,
                    [new ModelToolCall("call-1", "interrupt", "{}")])
            ]),
            session,
            new InterruptingTool(interruption));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => agent.RunAsync("do it", interruption.Token));

        var events = session.Events();
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
        using var session = new SessionFixture();
        var agent = CreateAgent(
            new ThrowingModelProvider(new InvalidOperationException("provider failed")),
            session);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.RunAsync("do it", TestContext.Current.CancellationToken));

        var error = Assert.Single(session.Events(), static e => TypeOf(e) == "error");
        Assert.Equal("provider failed", error.GetProperty("message").GetString());
        Assert.DoesNotContain(session.Events(), static e => TypeOf(e) == "interrupted");
    }

    [Fact]
    public async Task PersistsRedactedToolOutputNotTheSecret()
    {
        using var session = new SessionFixture();
        var agent = CreateAgent(
            new SequenceModelProvider([
                new ModelMessage(ModelRole.Assistant, null,
                    [new ModelToolCall("call-1", "secret", "{}")]),
                new ModelMessage(ModelRole.Assistant, "finished")
            ]),
            session,
            new SecretOutputTool());

        await agent.RunAsync("do it", TestContext.Current.CancellationToken);

        var tool = Assert.Single(session.Events(), static e =>
            TypeOf(e) == "message" && e.GetProperty("role").GetString() == "tool");
        var content = tool.GetProperty("content").GetString()!;
        Assert.Contains("API_KEY=[REDACTED]", content);
        Assert.DoesNotContain("hunter2", content);
        Assert.Contains("ask-turn-default-auto.txt", content);
    }

    [Fact]
    public async Task AWriteFailureDoesNotFailTheTurn()
    {
        using var session = new SessionFixture();
        session.Log.Dispose();
        var agent = CreateAgent(
            new SequenceModelProvider([new ModelMessage(ModelRole.Assistant, "finished")]),
            session);

        var result = await agent.RunAsync("do it", TestContext.Current.CancellationToken);

        Assert.Equal("finished", result.FinalResponse);
        Assert.Equal(AgentRunStatus.Completed, result.Status);
    }

    [Fact]
    public async Task AWriteFailureDoesNotMaskATurnError()
    {
        using var session = new SessionFixture();
        session.Log.Dispose();
        var agent = CreateAgent(
            new ThrowingModelProvider(new InvalidOperationException("provider failed")),
            session);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => agent.RunAsync("do it", TestContext.Current.CancellationToken));

        Assert.Equal("provider failed", exception.Message);
    }

    [Fact]
    public void SessionIdUsesUtcTimestampAndSixCharacterSuffix()
    {
        using var session = new SessionFixture(
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 22, 15, 4, 5, TimeSpan.Zero)));
        Assert.StartsWith("20260822T150405Z-", session.Log.Id);
        Assert.Matches(SessionIdPattern, session.Log.Id);
        Assert.Equal(Path.Combine(session.SessionsPath, session.Log.Id + ".jsonl"), session.Log.FilePath);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void CreatesSessionsDirectoryWithCurrentUserOnlyAcl()
    {
        using var sessions = new TemporaryDirectory();
        var root = Path.Combine(sessions.Path, "sessions");
        using var log = new SessionStore(root).Create(sessions.Path);
        log.Dispose();

        AssertCurrentUserOnlyAcl(root);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void TightensAclOnAnExistingSessionsDirectory()
    {
        using var sessions = new TemporaryDirectory();
        var root = Path.Combine(sessions.Path, "sessions");
        Directory.CreateDirectory(root);
        using var log = new SessionStore(root).Create(sessions.Path);
        log.Dispose();

        AssertCurrentUserOnlyAcl(root);
    }

    private static Agent CreateAgent(
        IModelProvider model,
        SessionFixture session,
        ITool? tool = null,
        AgentOptions? options = null) => new(
        model,
        new ToolRegistry([tool ?? new EchoTool()]),
        new PolicyApprovalService(ApprovalMode.Workspace, static (_, _) => ValueTask.FromResult(false)),
        new StaticContextProvider("test context"),
        session.Recorder,
        options ?? new AgentOptions("fake-model"),
        session.Workspace);

    private static Agent CreateAgent(
        IModelProvider model,
        SessionLog log,
        string workspace,
        IReadOnlyList<ModelMessage> conversation,
        AgentOptions options) => new(
        model,
        new ToolRegistry([new EchoTool()]),
        new PolicyApprovalService(ApprovalMode.Workspace, static (_, _) => ValueTask.FromResult(false)),
        new StaticContextProvider("test context"),
        new SessionRecorder(log),
        options,
        workspace,
        conversation);

    [SupportedOSPlatform("windows")]
    private static void AssertCurrentUserOnlyAcl(string directory)
    {
        var security = new DirectoryInfo(directory).GetAccessControl();
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

    private static JsonElement[] ReadEvents(string path) => ParseLines(ReadLines(path));

    private static string TypeOf(JsonElement element) => element.GetProperty("type").GetString()!;

    private static string Header(string id, string workspace, int schemaVersion = 1)
    {
        var escapedWorkspace = workspace.Replace("\\", "\\\\");
        return $$"""{"type":"header","schema_version":{{schemaVersion}},"session_id":"{{id}}","created_at":"2026-08-22T15:04:05Z","workspace":"{{escapedWorkspace}}"}""";
    }

    private sealed class SessionFixture : IDisposable
    {
        private readonly TemporaryDirectory _workspace = new();
        private readonly TemporaryDirectory _sessions = new();

        public SessionFixture(TimeProvider? time = null)
        {
            Workspace = _workspace.Path;
            SessionsPath = _sessions.Path;
            Log = new SessionStore(SessionsPath, time).Create(Workspace);
            Recorder = new SessionRecorder(Log);
        }

        public string Workspace { get; }

        public string SessionsPath { get; }

        public SessionLog Log { get; }

        public SessionRecorder Recorder { get; }

        public JsonElement[] Events() => ReadEvents(Log.FilePath);

        public void Dispose()
        {
            Log.Dispose();
            _sessions.Dispose();
            _workspace.Dispose();
        }
    }

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

    private sealed class CapturingModelProvider(ModelMessage response) : IModelProvider
    {
        public ModelRequest? LastRequest { get; private set; }

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            await Task.Yield();
            yield return new ModelCompleted(response);
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
