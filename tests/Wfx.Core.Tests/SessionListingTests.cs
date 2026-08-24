using System.Text.Json;
using Wfx.Core;

namespace Wfx.Core.Tests;

public sealed class SessionListingTests
{
    [Fact]
    public void ListReportsWorkspaceTimestampsSizeAndTotal()
    {
        using var temp = new TemporaryDirectory();
        var store = new SessionStore(System.IO.Path.Combine(temp.Path, "sessions"));
        var workspaceA = System.IO.Path.Combine(temp.Path, "a");
        var workspaceB = System.IO.Path.Combine(temp.Path, "b");
        using (var log = store.Create(workspaceA))
        {
            log.WriteMessage(new ModelMessage(ModelRole.Assistant, "first"));
        }

        using (var log = store.Create(workspaceB))
        {
            log.WriteMessage(new ModelMessage(ModelRole.Assistant, "second"));
        }

        var listing = store.List();
        var sessions = listing.Sessions;
        Assert.Equal(2, sessions.Count);
        Assert.All(sessions, session =>
        {
            Assert.False(string.IsNullOrWhiteSpace(session.SessionId));
            Assert.NotNull(session.Workspace);
            Assert.NotNull(session.CreatedAt);
            Assert.True(session.SizeBytes > 0);
        });

        Assert.Equal(Path.GetFullPath(workspaceA),
            sessions.Single(session => session.Workspace == Path.GetFullPath(workspaceA)).Workspace);
        Assert.Equal(Path.GetFullPath(workspaceB),
            sessions.Single(session => session.Workspace == Path.GetFullPath(workspaceB)).Workspace);
        var leaseBytes = Directory.EnumerateFiles(
                System.IO.Path.Combine(temp.Path, "sessions"),
                "*.lock")
            .Sum(path => new FileInfo(path).Length);
        Assert.Equal(sessions.Sum(session => session.SizeBytes) + leaseBytes, listing.TotalSizeBytes);
    }

    [Fact]
    public void ListIsEmptyWhenNoSessions()
    {
        using var temp = new TemporaryDirectory();
        var store = new SessionStore(System.IO.Path.Combine(temp.Path, "sessions"));
        var listing = store.List();
        Assert.Empty(listing.Sessions);
        Assert.Equal(0, listing.TotalSizeBytes);
    }

    [Fact]
    public void ListSkipsMalformedFileWithNonHeaderFirstLine()
    {
        using var temp = new TemporaryDirectory();
        var sessionsRoot = System.IO.Path.Combine(temp.Path, "sessions");
        Directory.CreateDirectory(sessionsRoot);
        System.IO.File.WriteAllText(
            System.IO.Path.Combine(sessionsRoot, "20260822T000000Z-aaaaaa.jsonl"),
            "{not header");
        var store = new SessionStore(sessionsRoot);
        Assert.Empty(store.List().Sessions);
    }

    [Fact]
    public void ListSucceedsWhileLogHoldsTheFile()
    {
        using var temp = new TemporaryDirectory();
        var store = new SessionStore(System.IO.Path.Combine(temp.Path, "sessions"));
        var workspace = System.IO.Path.Combine(temp.Path, "workspace");

        using (var log = store.Create(workspace))
        {
            log.WriteMessage(new ModelMessage(ModelRole.Assistant, "in progress"));

            // The log is still open (FileShare.Read + FileAccess.Write). Listing must not block
            // or fail under that share arrangement.
            var listing = store.List();
            var session = Assert.Single(listing.Sessions);
            Assert.Equal(log.Id, session.SessionId);
            Assert.Equal(Path.GetFullPath(workspace), session.Workspace);
            Assert.True(session.SizeBytes > 0);
        }
    }

    [Fact]
    public void ListScansLegacyEmptyLeaseSidecarForAWorkspaceRebind()
    {
        using var temp = new TemporaryDirectory();
        var sessionsRoot = System.IO.Path.Combine(temp.Path, "sessions");
        Directory.CreateDirectory(sessionsRoot);
        const string sessionId = "20260822T000000Z-rebound";
        var originalWorkspace = System.IO.Path.Combine(temp.Path, "original").Replace("\\", "\\\\");
        var reboundWorkspace = System.IO.Path.Combine(temp.Path, "rebound").Replace("\\", "\\\\");
        System.IO.File.WriteAllText(
            System.IO.Path.Combine(sessionsRoot, sessionId + ".jsonl"),
            string.Join('\n',
            [
                $$"""{"type":"header","schema_version":1,"session_id":"{{sessionId}}","created_at":"2026-08-22T00:00:00Z","workspace":"{{originalWorkspace}}"}""",
                $$"""{"type":"workspace_rebound","workspace":"{{reboundWorkspace}}"}"""
            ]) + "\n");
        System.IO.File.WriteAllBytes(
            System.IO.Path.Combine(sessionsRoot, sessionId + ".lock"),
            []);
        var store = new SessionStore(sessionsRoot);

        var session = Assert.Single(store.List().Sessions);

        Assert.Equal(System.IO.Path.Combine(temp.Path, "rebound"), session.Workspace);
    }

    [Fact]
    public void ListDoesNotReadNeverReboundSessionHistory()
    {
        using var temp = new TemporaryDirectory();
        var store = new SessionStore(System.IO.Path.Combine(temp.Path, "sessions"));
        var workspace = System.IO.Path.Combine(temp.Path, "workspace");
        var log = store.Create(workspace);
        var path = log.FilePath;
        log.Dispose();
        System.IO.File.AppendAllText(path, "{malformed later history}\n");

        var session = Assert.Single(store.List().Sessions);

        Assert.Equal(Path.GetFullPath(workspace), session.Workspace);
    }

    [Fact]
    public void ListReportsLastEndpointFromLatestTurnStarted()
    {
        using var temp = new TemporaryDirectory();
        var store = new SessionStore(System.IO.Path.Combine(temp.Path, "sessions"));
        var workspace = System.IO.Path.Combine(temp.Path, "workspace");
        using (var log = store.Create(workspace))
        {
            WriteTurnStarted(log, workspace, new EndpointIdentity(null, "openai", "chat_completions", "first-model"));
            log.WriteMessage(new ModelMessage(ModelRole.Assistant, "first turn"));
            WriteTurnStarted(log, workspace, new EndpointIdentity("deep", "openrouter", "responses", "second-model"));
            log.WriteMessage(new ModelMessage(ModelRole.Assistant, "second turn"));
        }

        var session = Assert.Single(store.List().Sessions);

        Assert.Equal(
            new EndpointIdentity("deep", "openrouter", "responses", "second-model"),
            session.LastEndpoint);
    }

    [Fact]
    public void ListReportsNullEndpointForHeaderOnlySession()
    {
        using var temp = new TemporaryDirectory();
        var store = new SessionStore(System.IO.Path.Combine(temp.Path, "sessions"));
        using (store.Create(System.IO.Path.Combine(temp.Path, "workspace")))
        {
        }

        var session = Assert.Single(store.List().Sessions);

        Assert.Null(session.LastEndpoint);
    }

    [Fact]
    public void ListReportsEndpointWhenLeaseRequiresFullScan()
    {
        using var temp = new TemporaryDirectory();
        var sessionsRoot = System.IO.Path.Combine(temp.Path, "sessions");
        Directory.CreateDirectory(sessionsRoot);
        const string sessionId = "20260822T000000Z-scanned";
        var workspace = System.IO.Path.Combine(temp.Path, "workspace").Replace("\\", "\\\\");
        System.IO.File.WriteAllText(
            System.IO.Path.Combine(sessionsRoot, sessionId + ".jsonl"),
            string.Join('\n',
            [
                $$"""{"type":"header","schema_version":1,"session_id":"{{sessionId}}","created_at":"2026-08-22T00:00:00Z","workspace":"{{workspace}}"}""",
                """{"type":"turn_started","profile":"deep","provider":"openrouter","protocol":"responses","model":"scanned-model"}"""
            ]) + "\n");
        // A legacy empty lease sidecar forces the full-history scan path.
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(sessionsRoot, sessionId + ".lock"), []);
        var store = new SessionStore(sessionsRoot);

        var session = Assert.Single(store.List().Sessions);

        Assert.Equal(
            new EndpointIdentity("deep", "openrouter", "responses", "scanned-model"),
            session.LastEndpoint);
    }

    [Fact]
    public void ListToleratesMalformedTailWhenReadingEndpoint()
    {
        using var temp = new TemporaryDirectory();
        var store = new SessionStore(System.IO.Path.Combine(temp.Path, "sessions"));
        var workspace = System.IO.Path.Combine(temp.Path, "workspace");
        var log = store.Create(workspace);
        WriteTurnStarted(log, workspace, new EndpointIdentity(null, "openai", "chat_completions", "durable-model"));
        var path = log.FilePath;
        log.Dispose();
        System.IO.File.AppendAllText(path, "{malformed later history}\n");

        var session = Assert.Single(store.List().Sessions);

        Assert.Equal(
            new EndpointIdentity(null, "openai", "chat_completions", "durable-model"),
            session.LastEndpoint);
    }

    [Fact]
    public void ListFindsEndpointBeyondOneTailChunk()
    {
        using var temp = new TemporaryDirectory();
        var store = new SessionStore(System.IO.Path.Combine(temp.Path, "sessions"));
        var workspace = System.IO.Path.Combine(temp.Path, "workspace");
        using (var log = store.Create(workspace))
        {
            WriteTurnStarted(log, workspace, new EndpointIdentity(null, "openai", "chat_completions", "buried-model"));
            // More than one 64 KB tail-scan chunk of traffic after the last turn_started.
            log.WriteMessage(new ModelMessage(ModelRole.Assistant, new string('x', 160 * 1024)));
        }

        var session = Assert.Single(store.List().Sessions);

        Assert.Equal(
            new EndpointIdentity(null, "openai", "chat_completions", "buried-model"),
            session.LastEndpoint);
    }

    [Fact]
    public void ListReadsHeaderFieldsFromDisk()
    {
        using var temp = new TemporaryDirectory();
        var store = new SessionStore(System.IO.Path.Combine(temp.Path, "sessions"));
        var workspace = System.IO.Path.Combine(temp.Path, "workspace");
        string sessionId;
        using (var log = store.Create(workspace))
        {
            sessionId = log.Id;
            log.WriteMessage(new ModelMessage(ModelRole.Assistant, "hello"));
        }

        // Verify the listed summary reflects what is actually on disk in the header.
        var path = System.IO.Path.Combine(System.IO.Path.Combine(temp.Path, "sessions"), sessionId + ".jsonl");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var headerLine = reader.ReadLine();
        using var document = JsonDocument.Parse(headerLine!);
        var root = document.RootElement;

        var summary = Assert.Single(store.List().Sessions);
        Assert.Equal(root.GetProperty("session_id").GetString(), summary.SessionId);
        Assert.Equal(root.GetProperty("workspace").GetString(), summary.Workspace);
        Assert.Equal(new FileInfo(path).Length, summary.SizeBytes);
    }

    // Writes a typed turn_started event the way the agent loop does, so listing exercises the
    // v2 transcript shape rather than a hand-written line.
    private static void WriteTurnStarted(SessionLog log, string workspace, EndpointIdentity endpoint) =>
        log.WriteAgentEvent(
            new TurnStartedEvent(log.Id, workspace, endpoint, ApprovalMode.Workspace, DateTimeOffset.UtcNow));
}
