namespace Wfx.Core;

/// <summary>
/// Records agent-loop observer events as JSONL lines on a <see cref="SessionLog"/>.
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
        _log.WriteTurnStarted(endpoint);
        return ValueTask.CompletedTask;
    }

    public ValueTask OnMessageAsync(ModelMessage message, CancellationToken cancellationToken)
    {
        _log.WriteMessage(message);
        return ValueTask.CompletedTask;
    }

    public ValueTask OnUsageAsync(ModelUsage usage, CancellationToken cancellationToken)
    {
        _log.WriteUsage(usage);
        return ValueTask.CompletedTask;
    }

    public ValueTask OnTurnInterruptedAsync(CancellationToken cancellationToken)
    {
        _log.WriteInterrupted();
        return ValueTask.CompletedTask;
    }

    public ValueTask OnTurnErrorAsync(Exception exception, CancellationToken cancellationToken)
    {
        _log.WriteError(exception);
        return ValueTask.CompletedTask;
    }
}
