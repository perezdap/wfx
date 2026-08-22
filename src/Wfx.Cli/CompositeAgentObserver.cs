using Wfx.Core;

namespace Wfx.Cli;

internal sealed class CompositeAgentObserver(params IAgentObserver[] observers) : IAgentObserver
{
    public ValueTask OnModelTextAsync(string text, CancellationToken cancellationToken) =>
        FanOut(observer => observer.OnModelTextAsync(text, cancellationToken));

    public ValueTask OnToolStartedAsync(
        string name,
        string argumentsJson,
        ApprovalLevel level,
        CancellationToken cancellationToken) =>
        FanOut(observer => observer.OnToolStartedAsync(name, argumentsJson, level, cancellationToken));

    public ValueTask OnToolCompletedAsync(
        string name,
        ToolResult result,
        TimeSpan duration,
        CancellationToken cancellationToken) =>
        FanOut(observer => observer.OnToolCompletedAsync(name, result, duration, cancellationToken));

    public ValueTask OnToolRejectedAsync(
        string name,
        string argumentsJson,
        string reason,
        CancellationToken cancellationToken) =>
        FanOut(observer => observer.OnToolRejectedAsync(name, argumentsJson, reason, cancellationToken));

    public ValueTask OnTurnStartedAsync(EndpointIdentity endpoint, CancellationToken cancellationToken) =>
        FanOut(observer => observer.OnTurnStartedAsync(endpoint, cancellationToken));

    public ValueTask OnMessageAsync(ModelMessage message, CancellationToken cancellationToken) =>
        FanOut(observer => observer.OnMessageAsync(message, cancellationToken));

    public ValueTask OnUsageAsync(ModelUsage usage, CancellationToken cancellationToken) =>
        FanOut(observer => observer.OnUsageAsync(usage, cancellationToken));

    public ValueTask OnTurnInterruptedAsync(CancellationToken cancellationToken) =>
        FanOut(observer => observer.OnTurnInterruptedAsync(cancellationToken));

    public ValueTask OnTurnErrorAsync(Exception exception, CancellationToken cancellationToken) =>
        FanOut(observer => observer.OnTurnErrorAsync(exception, cancellationToken));

    private async ValueTask FanOut(Func<IAgentObserver, ValueTask> action)
    {
        foreach (var observer in observers)
        {
            await action(observer).ConfigureAwait(false);
        }
    }
}
