using Wfx.Core;

namespace Wfx.Cli;

/// <summary>
/// Renders a turn for a human on stderr (ADR 0008): prose at column zero, tool calls indented
/// and dimmed, and a blank line between blocks so nothing runs together.
/// </summary>
internal sealed class ConsoleAgentObserver(
    bool verbose,
    bool debug,
    bool quiet,
    bool unicode = true,
    IReadOnlyList<string>? secrets = null,
    AnsiPalette palette = default) : IAgentObserver
{
    private const string ToolIndent = "  ";

    private const string DetailIndent = "    ";

    private readonly string _marker = unicode ? ConsoleText.Marker : ConsoleText.AsciiMarker;

    private readonly MarkdownStreamWriter _prose = new(Console.Error, palette, unicode);

    private Block _block = Block.None;

    private enum Block
    {
        None,
        Prose,
        Tools
    }

    public ValueTask OnModelTextAsync(string text, CancellationToken cancellationToken)
    {
        if (text.Length > 0)
        {
            BeginBlock(Block.Prose);
            _prose.Write(text);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask OnToolStartedAsync(
        string name,
        string argumentsJson,
        ApprovalLevel level,
        CancellationToken cancellationToken)
    {
        if (!quiet)
        {
            WriteToolCall(name, argumentsJson, verbose ? $" [{level}]" : string.Empty);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask OnToolRejectedAsync(
        string name,
        string argumentsJson,
        string reason,
        CancellationToken cancellationToken)
    {
        if (!quiet)
        {
            WriteToolCall(name, argumentsJson, string.Empty);
            WriteLine(DetailIndent + palette.Red($"skipped: {ToolCallSummary.DescribeText(reason, secrets: secrets)}"));
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask OnToolCompletedAsync(
        string name,
        ToolResult result,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        if (quiet)
        {
            return ValueTask.CompletedTask;
        }

        if (verbose)
        {
            WriteLine(DetailIndent + palette.Dim(
                $"{(result.Success ? "completed" : "failed")} in {duration.TotalMilliseconds:F0} ms"));
        }
        else if (!result.Success)
        {
            WriteLine(DetailIndent + palette.Red(
                $"failed: {ToolCallSummary.DescribeText(result.Error ?? "unknown error", secrets: secrets)}"));
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

    public ValueTask OnTurnCompletedAsync(CancellationToken cancellationToken)
    {
        EndTurn();
        return ValueTask.CompletedTask;
    }

    public ValueTask OnTurnInterruptedAsync(CancellationToken cancellationToken)
    {
        EndTurn();
        return ValueTask.CompletedTask;
    }

    public ValueTask OnTurnErrorAsync(Exception exception, CancellationToken cancellationToken)
    {
        EndTurn();
        return ValueTask.CompletedTask;
    }

    private void WriteToolCall(string name, string argumentsJson, string suffix)
    {
        BeginBlock(Block.Tools);
        var call = ToolCallSummary.Describe(name, argumentsJson, secrets: secrets);
        WriteLine(ToolIndent + palette.Dim($"{_marker} {call}{suffix}"));
    }

    /// <summary>
    /// Opens a new block, closing the previous one and separating the two with a blank line.
    /// Consecutive lines of the same kind stay together.
    /// </summary>
    private void BeginBlock(Block next)
    {
        if (_block == next)
        {
            return;
        }

        if (_block is Block.Prose)
        {
            _prose.EndBlock();
        }

        if (_block is not Block.None)
        {
            Console.Error.WriteLine();
        }

        _block = next;
    }

    /// <summary>Closes the turn so the prompt or the next turn starts on a clean line.</summary>
    private void EndTurn()
    {
        if (_block is Block.Prose)
        {
            _prose.EndBlock();
        }

        _block = Block.None;
    }

    private void WriteLine(string line) => Console.Error.WriteLine(ConsoleText.ForConsole(line, unicode));
}
