using Wfx.Core;

namespace Wfx.Cli;

internal sealed class ConsoleAgentObserver(
    bool verbose,
    bool debug,
    bool unicode = true,
    IReadOnlyList<string>? secrets = null) : IAgentObserver
{
    private readonly string _marker = unicode ? ConsoleText.Marker : ConsoleText.AsciiMarker;

    public ValueTask OnModelTextAsync(string text, CancellationToken cancellationToken)
    {
        Console.Write(text);
        return ValueTask.CompletedTask;
    }

    public ValueTask OnToolStartedAsync(
        string name,
        string argumentsJson,
        ApprovalLevel level,
        CancellationToken cancellationToken)
    {
        var call = ToolCallSummary.Describe(name, argumentsJson, secrets: secrets);
        WriteLine($"{_marker} {call}{(verbose ? $" [{level}]" : string.Empty)}");
        return ValueTask.CompletedTask;
    }

    public ValueTask OnToolRejectedAsync(
        string name,
        string argumentsJson,
        string reason,
        CancellationToken cancellationToken)
    {
        WriteLine($"{_marker} {ToolCallSummary.Describe(name, argumentsJson, secrets: secrets)}");
        WriteLine($"  skipped: {ToolCallSummary.DescribeText(reason, secrets: secrets)}");
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
            WriteLine($"  {(result.Success ? "completed" : "failed")} in {duration.TotalMilliseconds:F0} ms");
        }
        else if (!result.Success)
        {
            WriteLine($"  failed: {ToolCallSummary.DescribeText(result.Error ?? "unknown error", secrets: secrets)}");
        }

        if (debug && !string.IsNullOrWhiteSpace(result.Output))
        {
            var output = ToolCallSummary.RedactSecrets(result.Output, secrets);
            if (output.Length > 2_000)
            {
                output = output[..2_000] + ConsoleText.Ellipsis;
            }

            WriteLine(output);
        }

        return ValueTask.CompletedTask;
    }

    private void WriteLine(string line) => Console.Error.WriteLine(ConsoleText.ForConsole(line, unicode));
}
