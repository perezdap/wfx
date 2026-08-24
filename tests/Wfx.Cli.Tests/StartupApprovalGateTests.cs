using System.Net;
using System.Text;
using Wfx.Core;

namespace Wfx.Cli.Tests;

[Collection("Console")]
public sealed class StartupApprovalGateTests
{
    [Theory]
    [InlineData("always")]
    [InlineData("workspace")]
    public async Task RunRefusesRedirectedStdinWhenApprovalCanPrompt(string approval)
    {
        using var httpClient = CreateHttpClient(new UnexpectedRequestHandler());
        using var console = new ConsoleCapture();

        var exitCode = await CliRunner.RunAsync(
            TurnArguments("run", approval, "do it"),
            httpClient,
            new TestSessionStore(create: _ => throw new InvalidOperationException("No turn may start.")),
            TestContext.Current.CancellationToken,
            consoleEnvironment: FakeConsoleEnvironment.Redirected);

        Assert.Equal(3, exitCode);
        Assert.Contains($"approval is {approval}", console.ErrorText);
        Assert.Contains("--approval never", console.ErrorText);
        Assert.Contains("--yolo", console.ErrorText);
        Assert.Empty(console.Output.ToString());
    }

    [Theory]
    [InlineData("never")]
    [InlineData("yolo")]
    public async Task RunProceedsOnRedirectedStdinWhenApprovalCannotPrompt(string approval)
    {
        using var httpClient = CreateHttpClient(new CompletedTurnHandler());
        using var console = new ConsoleCapture();

        var exitCode = await CliRunner.RunAsync(
            TurnArguments("run", approval, "do it"),
            httpClient,
            new TestSessionStore(new SessionStore()),
            TestContext.Current.CancellationToken,
            consoleEnvironment: FakeConsoleEnvironment.Redirected);

        Assert.Equal(0, exitCode);
        Assert.Contains("finished", console.Output.ToString());
    }

    [Fact]
    public async Task RunProceedsWhenTheConsoleReportsATerminal()
    {
        using var httpClient = CreateHttpClient(new CompletedTurnHandler());
        using var console = new ConsoleCapture();

        var exitCode = await CliRunner.RunAsync(
            TurnArguments("run", "workspace", "do it"),
            httpClient,
            new TestSessionStore(new SessionStore()),
            TestContext.Current.CancellationToken,
            consoleEnvironment: FakeConsoleEnvironment.Terminal);

        Assert.Equal(0, exitCode);
        Assert.Contains("finished", console.Output.ToString());
    }

    [Theory]
    [InlineData("always")]
    [InlineData("workspace")]
    public async Task ResumeRefusesRedirectedStdinWhenApprovalCanPrompt(string approval)
    {
        using var console = new ConsoleCapture();

        var exitCode = await ResumeAsync(approval, FakeConsoleEnvironment.Redirected);

        Assert.Equal(3, exitCode);
        Assert.Contains($"approval is {approval}", console.ErrorText);
        Assert.Contains("--approval never", console.ErrorText);
        Assert.Contains("--yolo", console.ErrorText);
    }

    [Theory]
    [InlineData("never", false)]
    [InlineData("yolo", false)]
    [InlineData("workspace", true)]
    public async Task ResumeProceedsPastTheGate(string approval, bool terminal)
    {
        using var console = new ConsoleCapture();

        var exitCode = await ResumeAsync(
            approval,
            terminal ? FakeConsoleEnvironment.Terminal : FakeConsoleEnvironment.Redirected);

        Assert.NotEqual(3, exitCode);
        Assert.DoesNotContain("--approval never", console.ErrorText);
    }

    private static async Task<int> ResumeAsync(string approval, IConsoleEnvironment consoleEnvironment)
    {
        using var httpClient = CreateHttpClient(new CompletedTurnHandler());
        var sessions = Directory.CreateTempSubdirectory("wfx-cli-tests-");
        try
        {
            var store = new SessionStore(sessions.FullName);
            using (store.Create(WorkspaceInfo.Discover().Root))
            {
            }

            return await CliRunner.RunAsync(
                [
                    "resume",
                    "--provider", "local",
                    "--protocol", "chat_completions",
                    "--base-url", "https://example.test/v1",
                    "--model", "fake-model",
                    "--approval", approval
                ],
                httpClient,
                new TestSessionStore(store),
                TestContext.Current.CancellationToken,
                consoleEnvironment: consoleEnvironment);
        }
        finally
        {
            Directory.Delete(sessions.FullName, recursive: true);
        }
    }

    [Theory]
    [InlineData("sessions")]
    [InlineData("config")]
    [InlineData("models")]
    public async Task NonTurnCommandsAreNotGated(string command)
    {
        using var httpClient = CreateHttpClient(new UnexpectedRequestHandler());
        using var console = new ConsoleCapture();
        var sessions = Directory.CreateTempSubdirectory("wfx-cli-tests-");
        try
        {
            var exitCode = await CliRunner.RunAsync(
                [command, "--provider", "local", "--model", "fake-model", "--approval", "always"],
                httpClient,
                new TestSessionStore(new SessionStore(sessions.FullName)),
                TestContext.Current.CancellationToken,
                consoleEnvironment: FakeConsoleEnvironment.Redirected);

            Assert.Equal(0, exitCode);
            Assert.DoesNotContain("--approval never", console.ErrorText);
        }
        finally
        {
            Directory.Delete(sessions.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task HelpDocumentsTheGateAndItsExitCode()
    {
        using var httpClient = CreateHttpClient(new UnexpectedRequestHandler());
        using var console = new ConsoleCapture();

        var exitCode = await CliRunner.RunAsync(
            ["--help"],
            httpClient,
            new TestSessionStore(new SessionStore()),
            TestContext.Current.CancellationToken,
            consoleEnvironment: FakeConsoleEnvironment.Redirected);

        var help = console.Output.ToString();
        Assert.Equal(0, exitCode);
        Assert.Contains("3    wfx run or wfx resume refused to start", help);
        Assert.Contains("stdin is not a terminal", help);
        Assert.Contains("--approval never or --yolo", help);
    }

    private static string[] TurnArguments(string command, string approval, string prompt) =>
    [
        command,
        "--provider", "local",
        "--protocol", "chat_completions",
        "--base-url", "https://example.test/v1",
        "--model", "fake-model",
        "--approval", approval,
        "--no-session",
        prompt
    ];

    private static HttpClient CreateHttpClient(HttpMessageHandler handler) => new(handler)
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private sealed class UnexpectedRequestHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The gate must refuse before any model call.");
    }

    private sealed class CompletedTurnHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "data: {\"choices\":[{\"delta\":{\"content\":\"finished\"}}]}\n\ndata: [DONE]\n\n",
                    Encoding.UTF8,
                    "text/event-stream")
            });
    }
}
