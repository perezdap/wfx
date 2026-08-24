namespace Wfx.Core;

public sealed class SessionWorkspaceMismatchException(
    string sessionId,
    string recordedWorkspace,
    string currentWorkspace) : InvalidOperationException(
        $"Session '{sessionId}' is bound to workspace '{recordedWorkspace}', not '{currentWorkspace}'. Use --id '{sessionId}' --force to rebind it.")
{
    public string RecordedWorkspace { get; } = recordedWorkspace;

    public string CurrentWorkspace { get; } = currentWorkspace;
}

public sealed record ResumeSettingsResolution(
    WfxSettingsLayer Layer,
    string? OverridingProfile);

/// <summary>
/// Owns the exclusive lease and restored state for one resumed session.
/// </summary>
public sealed class SessionResume : IDisposable
{
    private bool _disposed;

    private SessionResume(SessionTranscript transcript, SessionLog log)
    {
        Transcript = transcript;
        Log = log;
    }

    public SessionTranscript Transcript { get; }

    public SessionLog Log { get; }

    public static SessionResume Open(
        ISessionStore store,
        WorkspaceInfo currentWorkspace,
        string? sessionId = null,
        bool force = false)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(currentWorkspace);
        var workspace = WorkspacePath.NormalizeRoot(currentWorkspace.Root);
        var selectedId = sessionId;
        if (selectedId is null)
        {
            selectedId = store.FindLatest(workspace)?.SessionId
                ?? throw new InvalidOperationException(
                    "No session for this workspace yet. Start one with 'wfx'.");
        }

        var selectedTranscript = store.Read(selectedId);
        if (!force)
        {
            ThrowIfWorkspaceMismatch(selectedId, selectedTranscript.Workspace, workspace);
        }

        // Re-read after acquiring the lease because another owner may have rebound the session
        // between the optimistic mismatch check and the fail-fast lease attempt.
        var log = store.Open(selectedId);
        try
        {
            var transcript = store.Read(selectedId);
            if (!SessionWorkspace.IsSame(transcript.Workspace, workspace))
            {
                if (!force)
                {
                    ThrowIfWorkspaceMismatch(selectedId, transcript.Workspace, workspace);
                }

                log.WriteWorkspaceRebound(workspace);
                transcript = transcript with { Workspace = workspace };
            }

            return new SessionResume(transcript, log);
        }
        catch
        {
            log.Dispose();
            throw;
        }
    }

    private static void ThrowIfWorkspaceMismatch(
        string sessionId,
        string recordedWorkspace,
        string currentWorkspace)
    {
        if (!SessionWorkspace.IsSame(recordedWorkspace, currentWorkspace))
        {
            throw new SessionWorkspaceMismatchException(
                sessionId,
                recordedWorkspace,
                currentWorkspace);
        }
    }

    public ResumeSettingsResolution ResolveSettings(WfxSettingsLayer cli)
    {
        ArgumentNullException.ThrowIfNull(cli);
        var recordedEndpoint = Transcript.LastEndpoint;
        if (recordedEndpoint is null)
        {
            return new ResumeSettingsResolution(cli, null);
        }

        if (cli.Profile is not null &&
            !string.Equals(cli.Profile, recordedEndpoint.Profile, StringComparison.OrdinalIgnoreCase))
        {
            return new ResumeSettingsResolution(cli, cli.Profile);
        }

        return new ResumeSettingsResolution(
            cli with
            {
                Profile = recordedEndpoint.Profile ?? cli.Profile,
                Provider = cli.Provider ?? recordedEndpoint.Provider,
                Protocol = cli.Protocol ?? recordedEndpoint.Protocol,
                Model = cli.Model ?? recordedEndpoint.Model
            },
            null);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Log.Dispose();
    }
}

internal static class SessionWorkspace
{
    public static bool IsSame(string recordedWorkspace, string workspace)
    {
        try
        {
            return string.Equals(
                WorkspacePath.NormalizeRoot(recordedWorkspace),
                WorkspacePath.NormalizeRoot(workspace),
                WorkspacePath.Comparison);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }
}
