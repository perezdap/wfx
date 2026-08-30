using System.Runtime.CompilerServices;
using System.Text.Json;
using Wfx.Core;

namespace Wfx.Cli.Tests;

[Collection("Console")]
public sealed class JsonEventStreamTests
{
    private static readonly string[][] JsonOutsideSubcommandArguments =
    [
        ["--json"]
    ];

    [Fact]
    public void JsonRequiresASubcommand()
    {
        Assert.True(CliArguments.Parse(["run", "--json", "prompt"]).Json);
        Assert.True(CliArguments.Parse(["resume", "--json"]).Json);
        Assert.True(CliArguments.Parse(["sessions", "--json"]).Json);
        Assert.True(CliArguments.Parse(["config", "--json"]).Json);
        Assert.True(CliArguments.Parse(["models", "--json"]).Json);

        foreach (var arguments in JsonOutsideSubcommandArguments)
        {
            var exception = Assert.Throws<ArgumentException>(() => CliArguments.Parse(arguments));
            Assert.Contains("wfx run --json", exception.Message);
            Assert.Contains("wfx resume --json", exception.Message);
        }
    }

    [Fact]
    public void QuietIsAcceptedInInteractiveModeAndEverySubcommand()
    {
        Assert.True(CliArguments.Parse(["--quiet"]).Quiet);
        Assert.True(CliArguments.Parse(["run", "--quiet", "prompt"]).Quiet);
        Assert.True(CliArguments.Parse(["resume", "--quiet"]).Quiet);
        Assert.True(CliArguments.Parse(["sessions", "--quiet"]).Quiet);
        Assert.True(CliArguments.Parse(["config", "--quiet"]).Quiet);
        Assert.True(CliArguments.Parse(["models", "--quiet"]).Quiet);

        var composed = CliArguments.Parse(["run", "--json", "--quiet", "prompt"]);
        Assert.True(composed.Json);
        Assert.True(composed.Quiet);
    }

    [Fact]
    public async Task JsonRunHelpStillShowsHelp()
    {
        using var httpClient = CliRunner.CreateUnexpectedHttpClient("Help must not call a model endpoint.");
        using var console = new ConsoleCapture();

        var exitCode = await CliRunner.RunAsync(
            ["run", "--json", "--help"],
            httpClient,
            new TestSessionStore(),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("Usage:", console.Output.ToString());
    }

    [Fact]
    public async Task HumanQuietSuppressesPresentationButPreservesWarnings()
    {
        using var sessions = new TemporaryDirectory();
        using var httpClient = CliRunner.CreateUnexpectedHttpClient("The injected provider must be used.");
        using var console = new ConsoleCapture();
        var provider = new QueuedModelProvider([
            new ModelCompleted(new ModelMessage(
                ModelRole.Assistant,
                null,
                [new ModelToolCall("call-1", "list_directory", "{\"path\":\".\"}")])),
            new ModelCompleted(new ModelMessage(ModelRole.Assistant, "done"))
        ]);

        var exitCode = await CliRunner.RunAsync(
            [
                "run", "--quiet", "--verbose", "--yolo", "--provider", "local",
                "--base-url", "https://example.test/v1", "--model", "fake-model", "inspect"
            ],
            httpClient,
            new SessionStore(sessions.Path),
            TestContext.Current.CancellationToken,
            modelProviderFactory: (_, _) => provider);

        Assert.Equal(0, exitCode);
        Assert.Contains("wfx: warning: approval is yolo", console.ErrorText);
        Assert.DoesNotContain("wfx: local/fake-model", console.ErrorText);
        Assert.DoesNotContain("wfx: session ", console.ErrorText);
        Assert.DoesNotContain("list_directory", console.ErrorText);
        Assert.DoesNotContain("completed in", console.ErrorText);
        Assert.DoesNotContain("[wfx] completed", console.ErrorText);
        Assert.DoesNotContain('\u001b', console.ErrorText);
    }

    [Fact]
    public async Task HumanQuietLeavesStdoutByteIdentical()
    {
        var baseline = await RunAsync(quiet: false);
        var quiet = await RunAsync(quiet: true);

        Assert.Equal(0, baseline.ExitCode);
        Assert.Equal(0, quiet.ExitCode);
        Assert.Equal(baseline.Output, quiet.Output);
        Assert.Contains("finished", quiet.Output);

        async Task<(int ExitCode, string Output)> RunAsync(bool quiet)
        {
            using var httpClient = CliRunner.CreateCompletedHttpClient();
            using var console = new ConsoleCapture();
            var arguments = new List<string>
            {
                "run", "--no-session", "--approval", "never", "--provider", "local",
                "--base-url", "https://example.test/v1", "--model", "fake-model", "inspect"
            };
            if (quiet)
            {
                arguments.Add("--quiet");
            }

            var exitCode = await CliRunner.RunAsync(
                [.. arguments],
                httpClient,
                new TestSessionStore(),
                TestContext.Current.CancellationToken,
                consoleEnvironment: FakeConsoleEnvironment.OutputRedirected);
            return (exitCode, console.Output.ToString());
        }
    }

    [Fact]
    public async Task InteractiveQuietRunsAHumanRepl()
    {
        using var httpClient = CliRunner.CreateUnexpectedHttpClient("Exiting the REPL must not call a model endpoint.");
        using var console = new ConsoleCapture("/exit\n");

        var exitCode = await CliRunner.RunAsync(
            [
                "--quiet", "--no-session", "--approval", "never", "--provider", "local",
                "--base-url", "https://example.test/v1", "--model", "fake-model"
            ],
            httpClient,
            new TestSessionStore(),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Empty(console.Output.ToString());
        Assert.Contains("WFX", console.ErrorText);
        Assert.DoesNotContain("\"event\"", console.ErrorText);
    }

    [Fact]
    public async Task HumanQuietPreservesTerminalFailureMessages()
    {
        using var sessions = new TemporaryDirectory();
        using var httpClient = CliRunner.CreateUnexpectedHttpClient("The injected provider must be used.");
        using var console = new ConsoleCapture();
        var provider = new QueuedModelProvider([
            new ModelCompleted(new ModelMessage(
                ModelRole.Assistant,
                "still working",
                [new ModelToolCall("call-1", "list_directory", "{\"path\":\".\"}")]))
        ]);

        var exitCode = await CliRunner.RunAsync(
            [.. RunJsonArguments("inspect").Where(static argument => argument != "--json"), "--quiet", "--max-iterations", "1"],
            httpClient,
            new SessionStore(sessions.Path),
            TestContext.Current.CancellationToken,
            modelProviderFactory: (_, _) => provider);

        Assert.Equal(4, exitCode);
        Assert.Contains("Iteration limit of 1 model iteration(s) reached", console.ErrorText);
        Assert.DoesNotContain("list_directory", console.ErrorText);
    }

    [Fact]
    public async Task RunJsonEmitsTurnStartedFirstAndACompletedTurn()
    {
        using var sessions = new TemporaryDirectory();
        using var httpClient = CliRunner.CreateUnexpectedHttpClient("The injected provider must be used.");
        using var console = new ConsoleCapture();
        var store = new SessionStore(sessions.Path);
        var provider = new SequenceModelProvider([
            new ModelCompleted(new ModelMessage(ModelRole.Assistant, "finished"), new ModelUsage(10, 4))
        ]);

        var exitCode = await CliRunner.RunAsync(
            RunJsonArguments("do it"),
            httpClient,
            store,
            TestContext.Current.CancellationToken,
            modelProviderFactory: (_, _) => provider);

        Assert.Equal(0, exitCode);
        var events = ParseLines(console.Output.ToString());
        var started = events[0];
        Assert.Equal("turn_started", started.GetProperty("event").GetString());
        Assert.Equal(1, started.GetProperty("schema_version").GetInt32());
        Assert.Equal(Assert.Single(store.List().Sessions).SessionId, started.GetProperty("session_id").GetString());
        Assert.Equal("never", started.GetProperty("approval_mode").GetString());
        Assert.Equal("local", started.GetProperty("endpoint").GetProperty("provider").GetString());
        Assert.Equal("turn_completed", events[^1].GetProperty("event").GetString());
        Assert.Equal("finished", events[^1].GetProperty("final_message").GetString());
        Assert.Equal(14, events[^1].GetProperty("total_usage").GetProperty("total_tokens").GetInt64());
    }

    [Fact]
    public async Task JsonQuietSuppressesPreStreamWarningsWithoutSuppressingEvents()
    {
        using var workspace = new TemporaryDirectory();
        using var httpClient = CliRunner.CreateUnexpectedHttpClient("The injected provider must be used.");
        using var console = new ConsoleCapture();
        var originalDirectory = Environment.CurrentDirectory;
        var userProfile = Path.Combine(workspace.Path, "profile");
        Directory.CreateDirectory(Path.Combine(workspace.Path, ".wfx"));
        Directory.CreateDirectory(Path.Combine(userProfile, ".wfx"));
        File.WriteAllText(
            Path.Combine(workspace.Path, ".wfx", "config.json"),
            """{ "provider": "local", "base_url": "https://example.test/v1", "model": "fake-model", "approval": "never" }""");
        File.WriteAllText(
            Path.Combine(userProfile, ".wfx", "config.json"),
            """{ "api_key": "user-secret" }""");
        Environment.CurrentDirectory = workspace.Path;
        try
        {
            var exitCode = await CliRunner.RunAsync(
                ["run", "--json", "--quiet", "inspect"],
                httpClient,
                new SessionStore(Path.Combine(workspace.Path, "sessions")),
                TestContext.Current.CancellationToken,
                userProfile,
                modelProviderFactory: (_, _) => new SequenceModelProvider([
                    new ModelCompleted(new ModelMessage(ModelRole.Assistant, "done"))
                ]));

            Assert.Equal(0, exitCode);
            Assert.Empty(console.ErrorText);
            var events = ParseLines(console.Output.ToString());
            Assert.Equal("turn_started", events[0].GetProperty("event").GetString());
            Assert.Equal("turn_completed", events[^1].GetProperty("event").GetString());
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
        }
    }

    [Fact]
    public async Task JsonQuietStillEmitsSkillWarnings()
    {
        using var workspace = new TemporaryDirectory();
        using var httpClient = CliRunner.CreateUnexpectedHttpClient("The injected provider must be used.");
        using var console = new ConsoleCapture();
        var originalDirectory = Environment.CurrentDirectory;
        var userProfile = Path.Combine(workspace.Path, "profile");
        Directory.CreateDirectory(Path.Combine(workspace.Path, ".wfx"));
        Directory.CreateDirectory(Path.Combine(userProfile, ".wfx"));
        File.WriteAllText(
            Path.Combine(workspace.Path, ".wfx", "config.json"),
            """{ "provider": "local", "base_url": "https://example.test/v1", "model": "fake-model", "approval": "never" }""");

        var skillDir = Path.Combine(userProfile, ".wfx", "skills", "bad");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(
            Path.Combine(skillDir, "SKILL.md"),
            """
            ---
            not a valid frontmatter line without colon value
            ---

            Body.
            """);

        Environment.CurrentDirectory = workspace.Path;
        try
        {
            var exitCode = await CliRunner.RunAsync(
                ["run", "--json", "--quiet", "inspect"],
                httpClient,
                new SessionStore(Path.Combine(workspace.Path, "sessions")),
                TestContext.Current.CancellationToken,
                userProfile,
                modelProviderFactory: (_, _) => new SequenceModelProvider([
                    new ModelCompleted(new ModelMessage(ModelRole.Assistant, "done"))
                ]));

            Assert.Equal(0, exitCode);
            Assert.Contains("wfx: warning:", console.ErrorText);
            var events = ParseLines(console.Output.ToString());
            Assert.Equal("turn_started", events[0].GetProperty("event").GetString());
            Assert.Equal("turn_completed", events[^1].GetProperty("event").GetString());
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
        }
    }

    [Fact]
    public async Task JsonAlonePreservesPreStreamWarningsWithoutTurnProgress()
    {
        using var workspace = new TemporaryDirectory();
        using var httpClient = CliRunner.CreateUnexpectedHttpClient("The injected provider must be used.");
        using var console = new ConsoleCapture();
        var originalDirectory = Environment.CurrentDirectory;
        var userProfile = Path.Combine(workspace.Path, "profile");
        Directory.CreateDirectory(Path.Combine(workspace.Path, ".wfx"));
        Directory.CreateDirectory(Path.Combine(userProfile, ".wfx"));
        File.WriteAllText(
            Path.Combine(workspace.Path, ".wfx", "config.json"),
            """{ "provider": "local", "base_url": "https://example.test/v1", "model": "fake-model", "approval": "never" }""");
        File.WriteAllText(
            Path.Combine(userProfile, ".wfx", "config.json"),
            """{ "api_key": "user-secret" }""");
        Environment.CurrentDirectory = workspace.Path;
        try
        {
            var provider = new QueuedModelProvider([
                new ModelCompleted(new ModelMessage(
                    ModelRole.Assistant,
                    null,
                    [new ModelToolCall("call-1", "list_directory", "{\"path\":\".\"}")])),
                new ModelCompleted(new ModelMessage(ModelRole.Assistant, "done"))
            ]);
            var exitCode = await CliRunner.RunAsync(
                ["run", "--json", "inspect"],
                httpClient,
                new SessionStore(Path.Combine(workspace.Path, "sessions")),
                TestContext.Current.CancellationToken,
                userProfile,
                modelProviderFactory: (_, _) => provider);

            Assert.Equal(0, exitCode);
            Assert.Contains("wfx: warning: Project base_url suppressed", console.ErrorText);
            Assert.DoesNotContain("list_directory", console.ErrorText);
            Assert.DoesNotContain("completed in", console.ErrorText);
            Assert.Contains(
                ParseLines(console.Output.ToString()),
                static item => item.GetProperty("event").GetString() == "tool_completed");
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
        }
    }

    [Fact]
    public async Task JsonQuietLeavesTheEventStreamByteIdentical()
    {
        using var sessions = new TemporaryDirectory();
        var baselinePath = Path.Combine(sessions.Path, "baseline");
        var quietPath = Path.Combine(sessions.Path, "quiet");
        var workspace = WorkspaceInfo.Discover().Root;
        var baselineStore = new SessionStore(baselinePath);
        string sessionId;
        using (var session = baselineStore.Create(workspace))
        {
            sessionId = session.Id;
        }

        Directory.CreateDirectory(quietPath);
        File.Copy(
            Path.Combine(baselinePath, sessionId + ".jsonl"),
            Path.Combine(quietPath, sessionId + ".jsonl"));
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero));

        var baseline = await RunResumeAsync(baselineStore, quiet: false);
        var quiet = await RunResumeAsync(new SessionStore(quietPath), quiet: true);

        Assert.Equal(0, baseline.ExitCode);
        Assert.Equal(0, quiet.ExitCode);
        Assert.Equal(baseline.Output, quiet.Output);
        Assert.Empty(baseline.Error);
        Assert.Empty(quiet.Error);

        async Task<(int ExitCode, string Output, string Error)> RunResumeAsync(
            ISessionStore store,
            bool quiet)
        {
            using var httpClient = CliRunner.CreateUnexpectedHttpClient("The injected provider must be used.");
            using var console = new ConsoleCapture("continue\n");
            var arguments = new List<string>
            {
                "resume", "--id", sessionId, "--json", "--approval", "never", "--provider", "local",
                "--base-url", "https://example.test/v1", "--model", "fake-model"
            };
            if (quiet)
            {
                arguments.Add("--quiet");
            }

            var exitCode = await CliRunner.RunAsync(
                [.. arguments],
                httpClient,
                store,
                TestContext.Current.CancellationToken,
                modelProviderFactory: (_, _) => new SequenceModelProvider([
                    new ModelCompleted(new ModelMessage(ModelRole.Assistant, "done"), new ModelUsage(3, 2))
                ]),
                timeProvider: time);
            return (exitCode, console.Output.ToString(), console.ErrorText);
        }
    }

    [Fact]
    public async Task ToolEventsPreserveOrderCallIdsAndRawArguments()
    {
        using var sessions = new TemporaryDirectory();
        using var httpClient = CliRunner.CreateUnexpectedHttpClient("The injected provider must be used.");
        using var console = new ConsoleCapture();
        var provider = new QueuedModelProvider([
            new ModelCompleted(new ModelMessage(
                ModelRole.Assistant,
                null,
                [new ModelToolCall("call-rejected", "missing_tool", "{not-json")]
            )),
            new ModelCompleted(new ModelMessage(
                ModelRole.Assistant,
                null,
                [new ModelToolCall("call-completed", "list_directory", "{\"path\":\".\"}")]
            )),
            new ModelCompleted(new ModelMessage(ModelRole.Assistant, "done"))
        ]);

        var exitCode = await CliRunner.RunAsync(
            RunJsonArguments("inspect"),
            httpClient,
            new SessionStore(sessions.Path),
            TestContext.Current.CancellationToken,
            modelProviderFactory: (_, _) => provider);

        Assert.Equal(0, exitCode);
        var toolEvents = ParseLines(console.Output.ToString())
            .Where(static item => item.GetProperty("event").GetString()!.StartsWith("tool_", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(["tool_rejected", "tool_started", "tool_completed"],
            toolEvents.Select(static item => item.GetProperty("event").GetString()));
        Assert.Equal("call-rejected", toolEvents[0].GetProperty("call_id").GetString());
        Assert.Equal("{not-json", toolEvents[0].GetProperty("arguments_json").GetString());
        Assert.Equal(JsonValueKind.String, toolEvents[0].GetProperty("arguments_json").ValueKind);
        Assert.Equal("call-completed", toolEvents[1].GetProperty("call_id").GetString());
        Assert.Equal("call-completed", toolEvents[2].GetProperty("call_id").GetString());
        Assert.False(toolEvents[2].GetProperty("result").GetProperty("is_error").GetBoolean());
    }

    [Fact]
    public async Task ResumeJsonStreamsOnePromptUnderTheExistingSessionId()
    {
        using var sessions = new TemporaryDirectory();
        using var httpClient = CliRunner.CreateUnexpectedHttpClient("The injected provider must be used.");
        using var console = new ConsoleCapture("continue\n");
        var store = new SessionStore(sessions.Path);
        var workspace = WorkspaceInfo.Discover().Root;
        var session = store.Create(workspace);
        var sessionId = session.Id;
        // Seed the prior turn through the typed event path, as the agent loop does.
        await new SessionRecorder(session).OnEventAsync(
            new TurnStartedEvent(
                sessionId,
                workspace,
                new EndpointIdentity(null, "local", "chat_completions", "fake-model"),
                ApprovalMode.Never,
                DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken);
        session.Dispose();
        var provider = new SequenceModelProvider([
            new ModelCompleted(new ModelMessage(ModelRole.Assistant, "resumed"))
        ]);

        var exitCode = await CliRunner.RunAsync(
            [
                "resume", "--id", sessionId, "--json", "--approval", "never", "--provider", "local",
                "--base-url", "https://example.test/v1", "--model", "fake-model"
            ],
            httpClient,
            store,
            TestContext.Current.CancellationToken,
            modelProviderFactory: (_, _) => provider);

        Assert.Equal(0, exitCode);
        var events = ParseLines(console.Output.ToString());
        Assert.Equal("turn_started", events[0].GetProperty("event").GetString());
        Assert.Equal(sessionId, events[0].GetProperty("session_id").GetString());
        Assert.Equal("resumed", events[^1].GetProperty("final_message").GetString());
    }

    [Fact]
    public async Task RedirectionSuppressesTerminalDecorationWithoutInferringJsonOrQuiet()
    {
        using var sessions = new TemporaryDirectory();
        using var httpClient = CliRunner.CreateCompletedHttpClient();
        using var console = new ConsoleCapture();

        var exitCode = await CliRunner.RunAsync(
            [
                "run", "--verbose", "--approval", "never", "--provider", "local",
                "--base-url", "https://example.test/v1", "--model", "fake-model", "inspect"
            ],
            httpClient,
            new SessionStore(sessions.Path),
            TestContext.Current.CancellationToken,
            consoleEnvironment: FakeConsoleEnvironment.Redirected);

        Assert.Equal(0, exitCode);
        Assert.Contains("finished", console.Output.ToString());
        Assert.DoesNotContain("\"event\":\"turn_started\"", console.Output.ToString());
        Assert.DoesNotContain('\u001b', console.Output.ToString());
        Assert.DoesNotContain('\u001b', console.ErrorText);
        Assert.Contains("[wfx] completed", console.ErrorText);
    }

    [Fact]
    public async Task MaxIterationsJsonReturnsFourAndEmitsInterruption()
    {
        using var sessions = new TemporaryDirectory();
        using var httpClient = CliRunner.CreateUnexpectedHttpClient("The injected provider must be used.");
        using var console = new ConsoleCapture();
        var provider = new QueuedModelProvider([
            new ModelCompleted(new ModelMessage(
                ModelRole.Assistant,
                "still working",
                [new ModelToolCall("call-1", "list_directory", "{\"path\":\".\"}")]))
        ]);

        var exitCode = await CliRunner.RunAsync(
            [.. RunJsonArguments("inspect"), "--max-iterations", "1"],
            httpClient,
            new SessionStore(sessions.Path),
            TestContext.Current.CancellationToken,
            modelProviderFactory: (_, _) => provider);

        Assert.Equal(4, exitCode);
        var interrupted = ParseLines(console.Output.ToString())[^1];
        Assert.Equal("turn_interrupted", interrupted.GetProperty("event").GetString());
        Assert.Equal("max_iterations", interrupted.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task MaxIterationsTextReturnsFour()
    {
        using var sessions = new TemporaryDirectory();
        using var httpClient = CliRunner.CreateUnexpectedHttpClient("The injected provider must be used.");
        using var console = new ConsoleCapture();
        var provider = new QueuedModelProvider([
            new ModelCompleted(new ModelMessage(
                ModelRole.Assistant,
                "still working",
                [new ModelToolCall("call-1", "list_directory", "{\"path\":\".\"}")]))
        ]);

        var exitCode = await CliRunner.RunAsync(
            [.. RunJsonArguments("inspect").Where(static argument => argument != "--json"), "--max-iterations", "1"],
            httpClient,
            new SessionStore(sessions.Path),
            TestContext.Current.CancellationToken,
            modelProviderFactory: (_, _) => provider);

        Assert.Equal(4, exitCode);
        Assert.Contains("Iteration limit of 1 model iteration(s) reached", console.ErrorText);
    }

    [Fact]
    public async Task ProviderErrorJsonReturnsFiveAndEmitsClassifiedError()
    {
        using var sessions = new TemporaryDirectory();
        using var httpClient = CliRunner.CreateUnexpectedHttpClient("The injected provider must be used.");
        using var console = new ConsoleCapture();

        var exitCode = await CliRunner.RunAsync(
            RunJsonArguments("fail"),
            httpClient,
            new SessionStore(sessions.Path),
            TestContext.Current.CancellationToken,
            modelProviderFactory: (_, _) => new ThrowingModelProvider());

        Assert.Equal(5, exitCode);
        var error = ParseLines(console.Output.ToString())[^1];
        Assert.Equal("turn_error", error.GetProperty("event").GetString());
        Assert.Equal("provider_error", error.GetProperty("error").GetProperty("kind").GetString());
        Assert.Equal("provider failed", error.GetProperty("error").GetProperty("message").GetString());
        Assert.Contains("wfx: provider failed", console.ErrorText);
    }

    [Fact]
    public async Task ProviderErrorJsonQuietPreservesTheTerminalFailureMessage()
    {
        using var sessions = new TemporaryDirectory();
        using var httpClient = CliRunner.CreateUnexpectedHttpClient("The injected provider must be used.");
        using var console = new ConsoleCapture();

        var exitCode = await CliRunner.RunAsync(
            [.. RunJsonArguments("fail"), "--quiet"],
            httpClient,
            new SessionStore(sessions.Path),
            TestContext.Current.CancellationToken,
            modelProviderFactory: (_, _) => new ThrowingModelProvider());

        Assert.Equal(5, exitCode);
        Assert.Equal("turn_error", ParseLines(console.Output.ToString())[^1].GetProperty("event").GetString());
        Assert.Equal("wfx: provider failed" + Environment.NewLine, console.ErrorText);
    }

    [Fact]
    public async Task JsonWithoutASubcommandReturnsUsageError()
    {
        foreach (var arguments in JsonOutsideSubcommandArguments)
        {
            using var httpClient = CliRunner.CreateUnexpectedHttpClient("A usage error must not call a model endpoint.");
            using var console = new ConsoleCapture();
            var exitCode = await CliRunner.RunAsync(
                arguments,
                httpClient,
                new TestSessionStore(),
                TestContext.Current.CancellationToken);

            Assert.Equal(1, exitCode);
            Assert.Contains("wfx run --json", console.ErrorText);
            Assert.Contains("wfx resume --json", console.ErrorText);
        }
    }

    [Fact]
    public async Task CancelledJsonTurnReturnsFourAndEmitsInterruption()
    {
        using var sessions = new TemporaryDirectory();
        using var httpClient = CliRunner.CreateUnexpectedHttpClient("The injected provider must be used.");
        using var console = new ConsoleCapture();
        using var cancellation = new CancellationTokenSource();
        var provider = new SelfCancellingModelProvider(cancellation);

        var exitCode = await CliRunner.RunAsync(
            RunJsonArguments("stop"),
            httpClient,
            new SessionStore(sessions.Path),
            cancellation.Token,
            modelProviderFactory: (_, _) => provider);

        Assert.Equal(4, exitCode);
        var interrupted = ParseLines(console.Output.ToString())[^1];
        Assert.Equal("turn_interrupted", interrupted.GetProperty("event").GetString());
        Assert.Equal("cancelled", interrupted.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task HelpDocumentsJsonFlagCredentialWarningAndTurnExitCodes()
    {
        using var httpClient = CliRunner.CreateUnexpectedHttpClient("Help must not call a model endpoint.");
        using var console = new ConsoleCapture();

        var exitCode = await CliRunner.RunAsync(
            ["--help"],
            httpClient,
            new TestSessionStore(),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        var help = console.Output.ToString();
        Assert.Contains("--json", help);
        Assert.Contains("--max-iterations <count>      Noninteractive loop limit", help);
        Assert.Contains("Interactive mode is unlimited", help);
        Assert.Contains("credential-adjacent", help);
        Assert.Contains("4    run stopped at maximum iterations, or JSON turn interrupted", help);
        Assert.Contains("5    JSON turn error", help);
    }

    [Fact]
    public async Task HelpDocumentsQuietAsAComposablePresentationFlag()
    {
        using var httpClient = CliRunner.CreateUnexpectedHttpClient("Help must not call a model endpoint.");
        using var console = new ConsoleCapture();

        var exitCode = await CliRunner.RunAsync(
            ["--help"],
            httpClient,
            new TestSessionStore(),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        var help = console.Output.ToString();
        Assert.Contains("--quiet", help);
        Assert.Contains("presentation flag", help, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("run, resume, sessions, config, and models", help);
        Assert.Contains("--json --quiet", help);
        Assert.Contains("does not change stdout", help);
    }

    [Fact]
    public async Task ObserverFlushesAfterEveryEvent()
    {
        using var output = new FlushTrackingWriter();
        var observer = new NdjsonAgentObserver(output);

        await observer.OnEventAsync(
            new MessageEvent(new ModelMessage(ModelRole.User, "one"), DateTimeOffset.UnixEpoch),
            TestContext.Current.CancellationToken);
        await observer.OnEventAsync(
            new MessageEvent(new ModelMessage(ModelRole.Assistant, "two"), DateTimeOffset.UnixEpoch),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, output.FlushCount);
        Assert.Equal(2, ParseLines(output.ToString()).Length);
    }

    [Fact]
    public async Task CanonicalEventsValidateAgainstPublishedSchema()
    {
        using var output = new StringWriter();
        var observer = new NdjsonAgentObserver(output);
        var at = new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);
        var events = new AgentEvent[]
        {
            new TurnStartedEvent(
                "20260824T120000Z-abc123",
                @"C:\workspace",
                new EndpointIdentity("work", "local", "chat_completions", "model"),
                ApprovalMode.Never,
                at),
            new MessageEvent(
                new ModelMessage(
                    ModelRole.Assistant,
                    "calling",
                    [new ModelToolCall("call-1", "list_directory", "{\"path\":\".\"}")],
                    ProviderItemsJson: "[{\"type\":\"reasoning\"}]"),
                at),
            new ToolStartedEvent("call-1", "list_directory", "{\"path\":\".\"}", ApprovalLevel.ReadOnly, at),
            new ToolCompletedEvent("call-1", "list_directory", ToolResult.Ok("README.md"), TimeSpan.FromMilliseconds(12), at),
            new ToolRejectedEvent("call-2", "write_file", "{bad", "denied", at),
            new UsageEvent(new ModelUsage(10, 4), at),
            new TurnCompletedEvent("20260824T120000Z-abc123", 2, "done", new ModelUsage(20, 8), at),
            new TurnInterruptedEvent("20260824T120000Z-abc123", AgentInterruptionReason.Timeout, at),
            new TurnErrorEvent(
                "20260824T120000Z-abc123",
                new AgentError(AgentErrorKind.ProviderError, "failed"),
                at)
        };
        foreach (var agentEvent in events)
        {
            await observer.OnEventAsync(agentEvent, TestContext.Current.CancellationToken);
        }

        using var schemaDocument = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "docs", "schemas", "wfx-events.v1.json"),
            TestContext.Current.CancellationToken));
        var schema = schemaDocument.RootElement;
        var definitions = schema.GetProperty("$defs");
        foreach (var instance in ParseLines(output.ToString()))
        {
            var eventName = instance.GetProperty("event").GetString()!;
            // Validate against the root schema so the oneOf event selection is exercised too.
            var errors = JsonSchemaValidator.Validate(instance, schema);
            Assert.True(errors.Count == 0, $"{eventName}: {string.Join("; ", errors)}");
        }

        foreach (var definition in definitions.EnumerateObject())
        {
            AssertVisibility(definition.Value, $"#/$defs/{definition.Name}");
        }
    }

    private static void AssertVisibility(JsonElement schema, string path)
    {
        if (schema.TryGetProperty("properties", out var properties))
        {
            foreach (var property in properties.EnumerateObject())
            {
                Assert.True(
                    property.Value.TryGetProperty("x-wfx-visibility", out var visibility) &&
                    visibility.GetString() is "public" or "internal",
                    $"{path}/properties/{property.Name} must be marked public or internal.");
                AssertVisibility(property.Value, $"{path}/properties/{property.Name}");
            }
        }

        if (schema.TryGetProperty("items", out var items))
        {
            AssertVisibility(items, $"{path}/items");
        }
    }

    [Fact]
    public async Task SkillIsListedInContextAndSkillToolReturnsBody()
    {
        using var sessions = new TemporaryDirectory();
        using var httpClient = CliRunner.CreateUnexpectedHttpClient("The injected provider must be used.");
        using var console = new ConsoleCapture();
        using var workspace = new TemporaryDirectory();
        var originalDirectory = Environment.CurrentDirectory;
        var userProfile = Path.Combine(workspace.Path, "profile");
        Directory.CreateDirectory(Path.Combine(workspace.Path, ".wfx"));
        Directory.CreateDirectory(Path.Combine(userProfile, ".wfx"));
        File.WriteAllText(
            Path.Combine(workspace.Path, ".wfx", "config.json"),
            """{ "provider": "local", "base_url": "https://example.test/v1", "model": "fake-model", "approval": "never" }""");

        var skillDir = Path.Combine(workspace.Path, ".wfx", "skills", "my-skill");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(
            Path.Combine(skillDir, "SKILL.md"),
            """
            ---
            name: my-skill
            description: Test skill for integration.
            ---

            # My Skill

            These are the full instructions.
            """);

        Environment.CurrentDirectory = workspace.Path;
        try
        {
            var provider = new QueuedModelProvider([
                new ModelCompleted(new ModelMessage(
                    ModelRole.Assistant,
                    null,
                    [new ModelToolCall("call-1", "skill", "{\"name\":\"my-skill\"}")])),
                new ModelCompleted(new ModelMessage(ModelRole.Assistant, "done"))
            ]);
            var exitCode = await CliRunner.RunAsync(
                ["run", "--json", "--quiet", "use the test skill"],
                httpClient,
                new SessionStore(sessions.Path),
                TestContext.Current.CancellationToken,
                userProfile,
                modelProviderFactory: (_, _) => provider);

            Assert.Equal(0, exitCode);
            Assert.Empty(console.ErrorText);
            var events = ParseLines(console.Output.ToString());
            Assert.Equal("turn_started", events[0].GetProperty("event").GetString());
            Assert.Equal("turn_completed", events[^1].GetProperty("event").GetString());

            var systemMessage = events.First(e =>
                e.GetProperty("event").GetString() == "message" &&
                e.GetProperty("role").GetString() == "system");
            var systemContent = systemMessage.GetProperty("content").GetString();
            Assert.NotNull(systemContent);
            Assert.Contains("Available skills:", systemContent);
            Assert.Contains("my-skill", systemContent);
            Assert.Contains("Test skill for integration.", systemContent);
            Assert.DoesNotContain("These are the full instructions.", systemContent);

            var skillTool = events.First(e =>
                e.GetProperty("event").GetString() == "tool_completed" &&
                e.GetProperty("name").GetString() == "skill");
            var resultContent = skillTool.GetProperty("result").GetProperty("content").GetString();
            Assert.Contains("# My Skill", resultContent);
            Assert.Contains("These are the full instructions.", resultContent);
            Assert.DoesNotContain("name: my-skill", resultContent);
            Assert.DoesNotContain("description: Test skill for integration.", resultContent);
        }
        finally
        {
            Environment.CurrentDirectory = originalDirectory;
        }
    }

    private static JsonElement[] ParseLines(string output) => output
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(static line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        })
        .ToArray();

    private static string[] RunJsonArguments(string prompt) =>
    [
        "run",
        "--json",
        "--approval", "never",
        "--provider", "local",
        "--base-url", "https://example.test/v1",
        "--model", "fake-model",
        prompt
    ];

    private sealed class SequenceModelProvider(IReadOnlyList<ModelStreamEvent> events) : IModelProvider
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (var modelEvent in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return modelEvent;
            }
        }
    }

    private sealed class QueuedModelProvider(IReadOnlyList<ModelCompleted> responses) : IModelProvider
    {
        private int _index;

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return responses[_index++];
        }
    }

    private sealed class SelfCancellingModelProvider(CancellationTokenSource source) : IModelProvider
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            source.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            yield break;
        }
    }

    private sealed class ThrowingModelProvider : IModelProvider
    {
        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw new HttpRequestException("provider failed");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class FlushTrackingWriter : StringWriter
    {
        public int FlushCount { get; private set; }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FlushCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = Directory.CreateTempSubdirectory("wfx-json-events-").FullName;
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
