namespace Wfx.Core;

/// <summary>
/// Records agent-loop observer events as JSONL lines on a <see cref="SessionLog"/>.
/// Persistence is observation: write failures are swallowed so a full disk or a
/// disposed log cannot fail the turn, and cannot mask the turn's original error
/// by throwing from <see cref="OnTurnErrorAsync"/>. Writes are synchronous; see
/// <see cref="SessionLog"/> for why the observer cancellation token is ignored.
/// </summary>
public sealed class SessionRecorder : IAgentObserver
{
    private readonly SessionLog _log;

    public SessionRecorder(SessionLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    public ValueTask OnTurnStartedAsync(EndpointIdentity endpoint, CancellationToken cancellationToken)
    {
        TryWrite(() => _log.WriteTurnStarted(endpoint));
        return ValueTask.CompletedTask;
    }

    public ValueTask OnMessageAsync(ModelMessage message, CancellationToken cancellationToken)
    {
        TryWrite(() => _log.WriteMessage(message));
        return ValueTask.CompletedTask;
    }

    public ValueTask OnUsageAsync(ModelUsage usage, CancellationToken cancellationToken)
    {
        TryWrite(() => _log.WriteUsage(usage));
        return ValueTask.CompletedTask;
    }

    public ValueTask OnTurnInterruptedAsync(CancellationToken cancellationToken)
    {
        TryWrite(_log.WriteInterrupted);
        return ValueTask.CompletedTask;
    }

    public ValueTask OnTurnErrorAsync(Exception exception, CancellationToken cancellationToken)
    {
        TryWrite(() => _log.WriteError(exception));
        return ValueTask.CompletedTask;
    }

    private static void TryWrite(Action write)
    {
        try
        {
            write();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException
            or UnauthorizedAccessException)
        {
        }
    }
}
