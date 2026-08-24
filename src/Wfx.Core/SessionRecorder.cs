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
    private readonly TimeProvider _time;

    public SessionRecorder(SessionLog log, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
        _time = timeProvider ?? TimeProvider.System;
    }

    public ValueTask OnEventAsync(AgentEvent agentEvent, CancellationToken cancellationToken)
    {
        var persistedEvent = agentEvent switch
        {
            TurnStartedEvent { SessionId.Length: 0 } started => started with { SessionId = _log.Id },
            TurnCompletedEvent { SessionId.Length: 0 } completed => completed with { SessionId = _log.Id },
            TurnInterruptedEvent { SessionId.Length: 0 } interrupted => interrupted with { SessionId = _log.Id },
            TurnErrorEvent { SessionId.Length: 0 } error => error with { SessionId = _log.Id },
            _ => agentEvent
        };
        TryWrite(() => _log.WriteAgentEvent(persistedEvent));
        return ValueTask.CompletedTask;
    }

    public ValueTask OnTurnStartedAsync(EndpointIdentity endpoint, CancellationToken cancellationToken) =>
        OnEventAsync(
            new TurnStartedEvent(_log.Id, string.Empty, endpoint, ApprovalMode.Always, _time.GetUtcNow()),
            cancellationToken);

    public ValueTask OnMessageAsync(ModelMessage message, CancellationToken cancellationToken) =>
        OnEventAsync(new MessageEvent(message, _time.GetUtcNow()), cancellationToken);

    public ValueTask OnUsageAsync(ModelUsage usage, CancellationToken cancellationToken) =>
        OnEventAsync(new UsageEvent(usage, _time.GetUtcNow()), cancellationToken);

    public ValueTask OnTurnInterruptedAsync(CancellationToken cancellationToken) =>
        OnEventAsync(
            new TurnInterruptedEvent(_log.Id, AgentInterruptionReason.Cancelled, _time.GetUtcNow()),
            cancellationToken);

    public ValueTask OnTurnErrorAsync(Exception exception, CancellationToken cancellationToken) =>
        OnEventAsync(
            new TurnErrorEvent(
                _log.Id,
                new AgentError(AgentErrorKind.ProviderError, exception.Message),
                _time.GetUtcNow())
            {
                Exception = exception
            },
            cancellationToken);

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
