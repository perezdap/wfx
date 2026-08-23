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
        Assert.Equal(sessions.Sum(session => session.SizeBytes), listing.TotalSizeBytes);
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
}
