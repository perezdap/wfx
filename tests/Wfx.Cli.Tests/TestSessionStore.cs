using Wfx.Core;

namespace Wfx.Cli.Tests;

internal sealed class TestSessionStore(
    Func<string, SessionLog>? create = null,
    Func<SessionListing>? list = null) : ISessionStore
{
    public SessionLog Create(string workspaceRoot) =>
        create?.Invoke(workspaceRoot)
        ?? throw new InvalidOperationException("Session creation was not expected.");

    public SessionListing List() =>
        list?.Invoke()
        ?? throw new InvalidOperationException("Session listing was not expected.");
}
