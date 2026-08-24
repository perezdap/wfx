using Wfx.Core;

namespace Wfx.Cli.Tests;

internal sealed class TestSessionStore(
    ISessionStore? inner = null,
    Func<string, SessionLog>? create = null,
    Func<SessionListing>? list = null) : ISessionStore
{
    public SessionLog Create(string workspaceRoot) =>
        create?.Invoke(workspaceRoot)
        ?? inner?.Create(workspaceRoot)
        ?? throw new InvalidOperationException("Session creation was not expected.");

    public SessionLog Open(string sessionId) =>
        inner?.Open(sessionId)
        ?? throw new InvalidOperationException("Session open was not expected.");

    public SessionTranscript Read(string sessionId) =>
        inner?.Read(sessionId)
        ?? throw new InvalidOperationException("Session read was not expected.");

    public SessionListing List() =>
        list?.Invoke()
        ?? inner?.List()
        ?? throw new InvalidOperationException("Session listing was not expected.");

    public long TotalSizeBytes() =>
        inner?.TotalSizeBytes()
        ?? List().TotalSizeBytes;

    public SessionSummary? FindLatest(string workspaceRoot) =>
        inner?.FindLatest(workspaceRoot);
}
