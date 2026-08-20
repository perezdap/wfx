using Wfx.Core;

namespace Wfx.Cli;

internal sealed class ConsoleAgentObserver(bool verbose, bool debug) : IAgentObserver
{
    public ValueTask OnModelTextAsync(string text, CancellationToken cancellationToken)
    {
        Console.Write(text);
        return ValueTask.CompletedTask;
    }

    public ValueTask OnToolStartedAsync(string name, ApprovalLevel level, CancellationToken cancellationToken)
    {
        Console.Error.WriteLine($"● {name}{(verbose ? $" [{level}]" : string.Empty)}");
        return ValueTask.CompletedTask;
    }

    public ValueTask OnToolCompletedAsync(
        string name,
        ToolResult result,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        if (verbose)
        {
            Console.Error.WriteLine($"  {(result.Success ? "completed" : "failed")} in {duration.TotalMilliseconds:F0} ms");
        }

        if (debug && !string.IsNullOrWhiteSpace(result.Output))
        {
            var output = result.Output.Length > 2_000 ? result.Output[..2_000] + "…" : result.Output;
            Console.Error.WriteLine(output);
        }

        return ValueTask.CompletedTask;
    }
}
