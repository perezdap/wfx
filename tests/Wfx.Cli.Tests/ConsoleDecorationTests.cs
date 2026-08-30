using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Wfx.Core;

namespace Wfx.Cli.Tests;

/// <summary>
/// Decoration on the stderr stream: blank lines between blocks, tool calls indented, and the
/// basic-eight palette gated on stderr rather than stdout (ADRs 0011 and 0009).
/// </summary>
[Collection("Console")]
public sealed partial class ConsoleDecorationTests
{
    [Fact]
    public async Task ToolCallsAreIndentedAndFencedByBlankLines()
    {
        var lines = StripAnsi(await RunTurnAsync(FakeConsoleEnvironment.Terminal)).Split('\n');

        var toolIndex = Array.FindIndex(lines, static line => line.Contains("list_directory", StringComparison.Ordinal));
        Assert.True(toolIndex > 0, "expected a tool line on stderr");
        Assert.StartsWith("  ", lines[toolIndex], StringComparison.Ordinal);
        Assert.Equal(string.Empty, lines[toolIndex - 1]);

        var narrationIndex = Array.FindIndex(lines, static line => line.StartsWith("I will look first.", StringComparison.Ordinal));
        var answerIndex = Array.FindIndex(lines, static line => line.StartsWith("Here is the answer.", StringComparison.Ordinal));
        Assert.True(narrationIndex >= 0 && narrationIndex < toolIndex);
        Assert.True(answerIndex > toolIndex);
        Assert.Equal(string.Empty, lines[answerIndex - 1]);
    }

    [Fact]
    public async Task ATerminalGetsAnsiAndProseItselfStaysUnstyled()
    {
        var stderr = await RunTurnAsync(FakeConsoleEnvironment.Terminal);

        Assert.Contains('\u001b', stderr);
        Assert.Contains("I will look first.", stderr);
        Assert.DoesNotContain("\u001b[1mI will look first.", stderr);
    }

    [Fact]
    public async Task ARedirectedStderrGetsNoAnsi()
    {
        var stderr = await RunTurnAsync(new FakeConsoleEnvironment(
            IsInputRedirected: false,
            IsOutputRedirected: false,
            IsErrorRedirected: true));

        Assert.DoesNotContain('\u001b', stderr);
    }

    /// <summary>
    /// `wfx run "..." &gt; notes.md` at a terminal: the file is bare, but the human watching the
    /// terminal still gets a decorated stderr. Decoration follows stderr, not stdout (ADR 0011).
    /// </summary>
    [Fact]
    public async Task RedirectingOnlyStdoutKeepsDecorationOnTheTerminal()
    {
        Assert.Contains('\u001b', await RunTurnAsync(FakeConsoleEnvironment.OutputRedirected));
    }

    [Fact]
    public async Task StdoutNeverCarriesAnsi()
    {
        using var console = new ConsoleCapture();

        await RunTurnAsync(FakeConsoleEnvironment.OutputRedirected, console);

        Assert.Contains("Here is the answer.", console.Output.ToString());
        Assert.DoesNotContain('\u001b', console.Output.ToString());
    }

    private static string StripAnsi(string text) => AnsiPattern().Replace(text, string.Empty).Replace("\r", string.Empty);

    [GeneratedRegex("\u001b\\[[0-9;]*m")]
    private static partial Regex AnsiPattern();

    private static async Task<string> RunTurnAsync(IConsoleEnvironment environment, ConsoleCapture? capture = null)
    {
        var console = capture ?? new ConsoleCapture();
        try
        {
            using var httpClient = CliRunner.CreateUnexpectedHttpClient("The injected provider must be used.");
            var provider = new NarratingModelProvider();
            await CliRunner.RunAsync(
                [
                    "run", "--yolo", "--no-session", "--provider", "local",
                    "--base-url", "https://example.test/v1", "--model", "fake-model", "inspect"
                ],
                httpClient,
                new TestSessionStore(),
                TestContext.Current.CancellationToken,
                consoleEnvironment: environment,
                modelProviderFactory: (_, _) => provider);
            return console.ErrorText;
        }
        finally
        {
            if (capture is null)
            {
                console.Dispose();
            }
        }
    }

    /// <summary>Narrates, calls one tool, then answers — the shape that used to run together.</summary>
    private sealed class NarratingModelProvider : IModelProvider
    {
        private int _index;

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (_index++ == 0)
            {
                yield return new ModelTextDelta("I will look first.");
                yield return new ModelCompleted(new ModelMessage(
                    ModelRole.Assistant,
                    "I will look first.",
                    [new ModelToolCall("call-1", "list_directory", "{\"path\":\".\"}")]));
                yield break;
            }

            yield return new ModelTextDelta("Here is the answer.");
            yield return new ModelCompleted(new ModelMessage(ModelRole.Assistant, "Here is the answer."));
        }
    }
}
