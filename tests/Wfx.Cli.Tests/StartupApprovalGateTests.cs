using Wfx.Core;

namespace Wfx.Cli.Tests;

[Collection("Console")]
public sealed class StartupApprovalGateTests
{
    private const string UnexpectedModelRequest = "The gate must refuse before any model call.";

    [Theory]
    [InlineData("always")]
    [InlineData("workspace")]
    public async Task RunRefusesRedirectedStdinWhenApprovalCanPrompt(string approval)
    {
        using var httpClient = CliRunner.CreateUnexpectedHttpClient(UnexpectedModelRequest);
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

    [Fact]
    public async Task RunRefusesRedirectedStdinWithImplicitAlwaysApproval()
    {
        using var httpClient = CliRunner.CreateUnexpectedHttpClient(UnexpectedModelRequest);
        using var console = new ConsoleCapture();
        var userProfile = Directory.CreateTempSubdirectory("wfx-cli-profile-");
        try
        {
            var exitCode = await CliRunner.RunAsync(
                TurnArguments("run", approval: null, "do it"),
                httpClient,
                new TestSessionStore(create: _ => throw new InvalidOperationException("No turn may start.")),
                TestContext.Current.CancellationToken,
                userProfile.FullName,
                FakeConsoleEnvironment.Redirected);

            Assert.Equal(3, exitCode);
            Assert.Contains("approval is always", console.ErrorText);
        }
        finally
        {
            Directory.Delete(userProfile.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task RunRefusesRedirectedStdinWithApprovalFromProfile()
    {
        using var httpClient = CliRunner.CreateUnexpectedHttpClient(UnexpectedModelRequest);
        using var console = new ConsoleCapture();
        var userProfile = Directory.CreateTempSubdirectory("wfx-cli-profile-");
        var configDirectory = Directory.CreateDirectory(Path.Combine(userProfile.FullName, ".wfx"));
        File.WriteAllText(
            Path.Combine(configDirectory.FullName, "config.json"),
            """
            {
              "profiles": {
                "unattended": { "approval": "workspace" }
              }
            }
            """);
        try
        {
            var exitCode = await CliRunner.RunAsync(
                [
                    "run",
                    "--provider", "local",
                    "--protocol", "chat_completions",
                    "--base-url", "https://example.test/v1",
                    "--model", "fake-model",
                    "--profile", "unattended",
                    "--no-session",
                    "do it"
                ],
                httpClient,
                new TestSessionStore(create: _ => throw new InvalidOperationException("No turn may start.")),
                TestContext.Current.CancellationToken,
                userProfile.FullName,
                FakeConsoleEnvironment.Redirected);

            Assert.Equal(3, exitCode);
            Assert.Contains("approval is workspace", console.ErrorText);
        }
        finally
        {
            Directory.Delete(userProfile.FullName, recursive: true);
        }
    }

    [Theory]
    [InlineData("never")]
    [InlineData("yolo")]
    public async Task RunProceedsOnRedirectedStdinWhenApprovalCannotPrompt(string approval)
    {
        using var httpClient = CliRunner.CreateCompletedHttpClient();
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
        using var httpClient = CliRunner.CreateCompletedHttpClient();
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
        using var console = new ConsoleCapture("continue\n/exit\n");

        var exitCode = await ResumeAsync(
            approval,
            terminal ? FakeConsoleEnvironment.Terminal : FakeConsoleEnvironment.Redirected);

        Assert.Equal(0, exitCode);
        Assert.Contains("finished", console.Output.ToString());
        Assert.DoesNotContain("--approval never", console.ErrorText);
    }

    [Fact]
    public async Task RefusedForcedResumeDoesNotRebindTheSession()
    {
        using var httpClient = CliRunner.CreateUnexpectedHttpClient(UnexpectedModelRequest);
        using var console = new ConsoleCapture();
        var sessions = Directory.CreateTempSubdirectory("wfx-cli-tests-");
        try
        {
            var store = new SessionStore(sessions.FullName);
            var created = store.Create(Path.Combine(Path.GetTempPath(), "wfx-other-workspace"));
            created.Dispose();
            var before = File.ReadAllText(created.FilePath);

            var exitCode = await CliRunner.RunAsync(
                [
                    "resume",
                    "--id", created.Id,
                    "--force",
                    "--provider", "local",
                    "--protocol", "chat_completions",
                    "--base-url", "https://example.test/v1",
                    "--model", "fake-model",
                    "--approval", "workspace"
                ],
                httpClient,
                new TestSessionStore(store),
                TestContext.Current.CancellationToken,
                consoleEnvironment: FakeConsoleEnvironment.Redirected);

            Assert.Equal(3, exitCode);
            var after = File.ReadAllText(created.FilePath);
            Assert.Equal(before, after);
            Assert.DoesNotContain("workspace_rebound", after);
        }
        finally
        {
            Directory.Delete(sessions.FullName, recursive: true);
        }
    }

    private static async Task<int> ResumeAsync(string approval, IConsoleEnvironment consoleEnvironment)
    {
        using var httpClient = CliRunner.CreateCompletedHttpClient();
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
        using var httpClient = CliRunner.CreateUnexpectedHttpClient(UnexpectedModelRequest);
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
        using var httpClient = CliRunner.CreateUnexpectedHttpClient(UnexpectedModelRequest);
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

    [Fact]
    public async Task HelpOutputIsNoWiderThanTheOptionsTable()
    {
        using var httpClient = CliRunner.CreateUnexpectedHttpClient(UnexpectedModelRequest);
        using var console = new ConsoleCapture();

        var exitCode = await CliRunner.RunAsync(
            ["--help"],
            httpClient,
            new TestSessionStore(new SessionStore()),
            TestContext.Current.CancellationToken,
            consoleEnvironment: FakeConsoleEnvironment.Redirected);

        Assert.Equal(0, exitCode);
        var lines = console.Output.ToString().Split('\n');
        // 93 is the width of the --protocol option line, the widest line of the help layout.
        // The approval-gate remediation must wrap instead of spilling past it (#63).
        Assert.All(
            lines,
            line =>
            {
                var trimmed = line.TrimEnd('\r');
                Assert.True(trimmed.Length <= 93, $"Help line is {trimmed.Length} chars wide: {trimmed}");
            });
    }

    private static string[] TurnArguments(string command, string? approval, string prompt)
    {
        string[] approvalArguments = approval is null ? [] : ["--approval", approval];
        return
        [
            command,
            "--provider", "local",
            "--protocol", "chat_completions",
            "--base-url", "https://example.test/v1",
            "--model", "fake-model",
            .. approvalArguments,
            "--no-session",
            prompt
        ];
    }
}
