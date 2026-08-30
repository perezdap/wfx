using System.Runtime.CompilerServices;
using Wfx.Core;

namespace Wfx.Cli.Tests;

/// <summary>
/// Human output is a stderr stream; stdout carries only a redirected final response (ADR 0008).
/// </summary>
[Collection("Console")]
public sealed class HumanOutputTests
{
    [Fact]
    public async Task RedirectedStdoutReceivesTheFinalResponseAndNothingElse()
    {
        using var console = new ConsoleCapture();

        var exitCode = await RunNarratedTurnAsync(console, FakeConsoleEnvironment.OutputRedirected);

        Assert.Equal(0, exitCode);
        Assert.Equal("Here is the answer.", console.Output.ToString().TrimEnd('\r', '\n'));
    }

    [Fact]
    public async Task NarrationNeverReachesStdout()
    {
        using var console = new ConsoleCapture();

        await RunNarratedTurnAsync(console, FakeConsoleEnvironment.OutputRedirected);

        Assert.DoesNotContain("I will look first.", console.Output.ToString());
        Assert.Contains("I will look first.", console.ErrorText);
    }

    [Fact]
    public async Task TerminalStdoutReceivesNothingAndStderrCarriesTheWholeTurn()
    {
        using var console = new ConsoleCapture();

        var exitCode = await RunNarratedTurnAsync(console, FakeConsoleEnvironment.Terminal);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, console.Output.ToString());
        Assert.Contains("I will look first.", console.ErrorText);
        Assert.Contains("Here is the answer.", console.ErrorText);
    }

    [Fact]
    public async Task AnIterationLimitLeavesRedirectedStdoutEmpty()
    {
        using var console = new ConsoleCapture();
        using var httpClient = CliRunner.CreateUnexpectedHttpClient("The injected provider must be used.");
        var provider = new ScriptedModelProvider([
            new Turn(["I will look first."], ToolCall: true),
            new Turn(["Still going."], ToolCall: true)
        ]);

        var exitCode = await CliRunner.RunAsync(
            [
                "run", "--yolo", "--no-session", "--max-iterations", "1", "--provider", "local",
                "--base-url", "https://example.test/v1", "--model", "fake-model", "inspect"
            ],
            httpClient,
            new TestSessionStore(),
            TestContext.Current.CancellationToken,
            consoleEnvironment: FakeConsoleEnvironment.OutputRedirected,
            modelProviderFactory: (_, _) => provider);

        Assert.Equal(4, exitCode);
        Assert.Equal(string.Empty, console.Output.ToString());
        Assert.Contains("I will look first.", console.ErrorText);
    }

    private static Task<int> RunNarratedTurnAsync(ConsoleCapture console, IConsoleEnvironment environment)
    {
        var httpClient = CliRunner.CreateUnexpectedHttpClient("The injected provider must be used.");
        var provider = new ScriptedModelProvider([
            new Turn(["I will look first."], ToolCall: true),
            new Turn(["Here is the answer."], ToolCall: false)
        ]);

        return CliRunner.RunAsync(
            [
                "run", "--yolo", "--no-session", "--provider", "local",
                "--base-url", "https://example.test/v1", "--model", "fake-model", "inspect"
            ],
            httpClient,
            new TestSessionStore(),
            TestContext.Current.CancellationToken,
            consoleEnvironment: environment,
            modelProviderFactory: (_, _) => provider);
    }

    private sealed record Turn(IReadOnlyList<string> Deltas, bool ToolCall);

    /// <summary>Streams text deltas before each completion, so narration reaches observers.</summary>
    private sealed class ScriptedModelProvider(IReadOnlyList<Turn> turns) : IModelProvider
    {
        private int _index;

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            var turn = turns[Math.Min(_index++, turns.Count - 1)];
            var text = string.Concat(turn.Deltas);
            foreach (var delta in turn.Deltas)
            {
                yield return new ModelTextDelta(delta);
            }

            yield return new ModelCompleted(new ModelMessage(
                ModelRole.Assistant,
                text,
                turn.ToolCall
                    ? [new ModelToolCall($"call-{_index}", "list_directory", "{\"path\":\".\"}")]
                    : null));
        }
    }
}
