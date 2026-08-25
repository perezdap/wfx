using System.Text;
using System.Text.Json;
using Wfx.Core;

namespace Wfx.Cli.Tests;

[Collection("Console")]
public sealed class SessionStartupTests
{
    [Fact]
    public async Task RunContinuesWhenSessionsPathIsAFile()
    {
        var directory = Directory.CreateTempSubdirectory("wfx-cli-tests-");
        var sessionsPath = Path.Combine(directory.FullName, "sessions");
        File.WriteAllText(sessionsPath, "not a directory");
        using var httpClient = CreateHttpClient();
        using var console = new ConsoleCapture();
        try
        {
            var store = new SessionStore(sessionsPath);
            var exitCode = await CliRunner.RunAsync(
                RunArguments,
                httpClient,
                store,
                TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            Assert.Contains("finished", console.Output.ToString());
            Assert.Contains("wfx: warning: Could not create session:", console.ErrorText);
            Assert.Contains("The invocation will continue without a session.", console.ErrorText);
            Assert.True(File.Exists(sessionsPath));
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task InteractiveContinuesWhenSessionCreationIsUnauthorized()
    {
        using var httpClient = CreateHttpClient();
        using var console = new ConsoleCapture("do it\n/exit\n");

        var exitCode = await CliRunner.RunAsync(
            InteractiveArguments,
            httpClient,
            new TestSessionStore(create: _ => throw new UnauthorizedAccessException("session ACL denied")),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("finished", console.Output.ToString());
        Assert.Contains(
            "wfx: warning: Could not create session: session ACL denied. The invocation will continue without a session.",
            console.ErrorText);
    }

    [Fact]
    public async Task InteractiveTurnContinuesBeyondTheDefaultIterationLimit()
    {
        using var httpClient = CliRunner.CreateUnexpectedHttpClient("The injected provider must be used.");
        using var console = new ConsoleCapture("do it\n/exit\n");
        var responses = Enumerable.Range(1, 25)
            .Select(static index => new ModelCompleted(new ModelMessage(
                ModelRole.Assistant,
                null,
                [new ModelToolCall($"call-{index}", "missing_tool", "{}")])))
            .Append(new ModelCompleted(new ModelMessage(ModelRole.Assistant, "finished")))
            .ToArray();
        var provider = new QueuedModelProvider(responses);

        var exitCode = await CliRunner.RunAsync(
            [.. InteractiveArguments, "--approval", "never", "--no-session"],
            httpClient,
            new TestSessionStore(),
            TestContext.Current.CancellationToken,
            modelProviderFactory: (_, _) => provider);

        Assert.Equal(0, exitCode);
        Assert.Equal(26, provider.RequestCount);
        Assert.DoesNotContain("Iteration limit", console.ErrorText);
    }

    [Fact]
    public async Task SessionAnnouncementFailureDisposesSessionAndFailsRun()
    {
        var directory = Directory.CreateTempSubdirectory("wfx-cli-tests-");
        using var httpClient = CreateHttpClient();
        var error = new SessionAnnouncementFailingWriter();
        using var console = new ConsoleCapture(error: error);
        SessionLog? openedSession = null;
        try
        {
            var store = new SessionStore(directory.FullName);
            var testStore = new TestSessionStore(create: workspace => openedSession = store.Create(workspace));
            var exitCode = await CliRunner.RunAsync(
                RunArguments,
                httpClient,
                testStore,
                TestContext.Current.CancellationToken);

            Assert.Equal(1, exitCode);
            Assert.Contains("wfx: session announcement failed", error.Text);
            Assert.NotNull(openedSession);
            using var exclusive = new FileStream(
                openedSession.FilePath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        finally
        {
            openedSession?.Dispose();
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task RunAnnouncesAndWritesSuccessfulSession()
    {
        var directory = Directory.CreateTempSubdirectory("wfx-cli-tests-");
        using var httpClient = CreateHttpClient();
        using var console = new ConsoleCapture();
        try
        {
            var store = new SessionStore(directory.FullName);
            var exitCode = await CliRunner.RunAsync(
                RunArguments,
                httpClient,
                store,
                TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            Assert.Contains("wfx: session ", console.ErrorText);
            Assert.DoesNotContain("wfx: warning: Could not create session", console.ErrorText);
            Assert.Single(Directory.GetFiles(directory.FullName, "*.jsonl"));
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task NoSessionDoesNotCreateOrAnnounceASession()
    {
        using var httpClient = CreateHttpClient();
        using var console = new ConsoleCapture();
        var createCalled = false;

        var store = new TestSessionStore(create: _ =>
        {
            createCalled = true;
            throw new InvalidOperationException("Session creation must be skipped.");
        });
        var exitCode = await CliRunner.RunAsync(
            NoSessionArguments,
            httpClient,
            store,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.False(createCalled);
        Assert.DoesNotContain("session ", console.ErrorText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResumeWithoutMatchingWorkspaceSessionReturnsClearError()
    {
        using var httpClient = CreateHttpClient();
        using var console = new ConsoleCapture();
        var sessions = Directory.CreateTempSubdirectory("wfx-cli-tests-");
        try
        {
            var exitCode = await CliRunner.RunAsync(
                ["resume", "--provider", "local", "--model", "fake-model"],
                httpClient,
                new TestSessionStore(new SessionStore(sessions.FullName)),
                TestContext.Current.CancellationToken);

            Assert.Equal(1, exitCode);
            Assert.Contains(
                "No session for this workspace yet. Start one with 'wfx'.",
                console.Error.ToString());
        }
        finally
        {
            Directory.Delete(sessions.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task ForceWithoutSessionIdReturnsClearError()
    {
        using var httpClient = CreateHttpClient();
        using var console = new ConsoleCapture();

        var exitCode = await CliRunner.RunAsync(
            ["resume", "--force"],
            httpClient,
            new TestSessionStore(new SessionStore()),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Contains("--force requires --id", console.Error.ToString());
    }

    [Fact]
    public async Task ResumeUnknownIdReturnsClearError()
    {
        using var httpClient = CreateHttpClient();
        using var console = new ConsoleCapture();
        var sessions = Directory.CreateTempSubdirectory("wfx-cli-tests-");
        try
        {
            var exitCode = await CliRunner.RunAsync(
                ["resume", "--id", "missing-session", "--provider", "local", "--model", "fake-model"],
                httpClient,
                new TestSessionStore(new SessionStore(sessions.FullName)),
                TestContext.Current.CancellationToken);

            Assert.Equal(1, exitCode);
            Assert.Contains("No session with ID 'missing-session'.", console.Error.ToString());
        }
        finally
        {
            Directory.Delete(sessions.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task ResumeRefusesAnotherWorkspaceAndForceRebindsIt()
    {
        using var httpClient = CreateHttpClient();
        using var console = new ConsoleCapture("/exit\n");
        var sessions = Directory.CreateTempSubdirectory("wfx-cli-tests-");
        var recordedWorkspace = Directory.CreateTempSubdirectory("wfx-recorded-workspace-");
        var store = new SessionStore(sessions.FullName);
        SessionLog? created = null;
        try
        {
            created = store.Create(recordedWorkspace.FullName);
            var sessionId = created.Id;
            var sessionPath = created.FilePath;
            created.Dispose();
            created = null;

            var refused = await CliRunner.RunAsync(
                ["resume", "--id", sessionId, "--provider", "local", "--model", "fake-model"],
                httpClient,
                new TestSessionStore(store),
                TestContext.Current.CancellationToken);

            Assert.Equal(1, refused);
            Assert.Contains(Path.GetFullPath(recordedWorkspace.FullName), console.Error.ToString());
            Assert.DoesNotContain("workspace_rebound", File.ReadAllText(sessionPath), StringComparison.Ordinal);

            var forced = await CliRunner.RunAsync(
                [
                    "resume",
                    "--id", sessionId,
                    "--force",
                    "--provider", "local",
                    "--model", "fake-model"
                ],
                httpClient,
                new TestSessionStore(store),
                TestContext.Current.CancellationToken);

            Assert.Equal(0, forced);
            Assert.Contains($"Resumed session: {sessionId}", console.Output.ToString());
            Assert.Contains("\"type\":\"workspace_rebound\"", File.ReadAllText(sessionPath));
            Assert.Equal(WorkspaceInfo.Discover().Root, store.Read(sessionId).Workspace);
        }
        finally
        {
            created?.Dispose();
            Directory.Delete(sessions.FullName, recursive: true);
            Directory.Delete(recordedWorkspace.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task ResumeReportsSessionInUseWhileSessionsListingStillWorks()
    {
        using var httpClient = CreateHttpClient();
        using var console = new ConsoleCapture();
        var sessions = Directory.CreateTempSubdirectory("wfx-cli-tests-");
        var store = new SessionStore(sessions.FullName);
        var created = store.Create(WorkspaceInfo.Discover().Root);
        var sessionId = created.Id;
        created.Dispose();
        try
        {
            using var held = SessionResume.Open(store, WorkspaceInfo.Discover(), sessionId);
            var resumeExitCode = await CliRunner.RunAsync(
                ["resume", "--id", sessionId, "--provider", "local", "--model", "fake-model"],
                httpClient,
                new TestSessionStore(store),
                TestContext.Current.CancellationToken);
            var sessionsExitCode = await CliRunner.RunAsync(
                ["sessions"],
                httpClient,
                new TestSessionStore(store),
                TestContext.Current.CancellationToken);

            Assert.Equal(1, resumeExitCode);
            Assert.Contains("session", console.Error.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("in use", console.Error.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, sessionsExitCode);
            Assert.Contains(sessionId, console.Output.ToString());
        }
        finally
        {
            Directory.Delete(sessions.FullName, recursive: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResumeReopensAndAppendsToTheSameSession(bool selectById)
    {
        using var httpClient = CreateHttpClient();
        using var console = new ConsoleCapture("next\n/exit\n");
        var sessions = Directory.CreateTempSubdirectory("wfx-cli-tests-");
        var store = new SessionStore(sessions.FullName);
        var workspace = WorkspaceInfo.Discover().Root;
        SessionLog? created = null;
        try
        {
            created = store.Create(workspace);
            var recorder = new SessionRecorder(created);
            await recorder.OnTurnStartedAsync(
                new EndpointIdentity(null, "local", "chat_completions", "fake-model"),
                CancellationToken.None);
            await recorder.OnMessageAsync(new ModelMessage(ModelRole.User, "previous"), CancellationToken.None);
            await recorder.OnMessageAsync(new ModelMessage(ModelRole.Assistant, "answer"), CancellationToken.None);
            created.Dispose();

            string[] resumeArguments = selectById
                ? [
                    "resume",
                    "--id", created.Id,
                    "--provider", "local",
                    "--protocol", "chat_completions",
                    "--base-url", "https://example.test/v1",
                    "--model", "fake-model"
                ]
                : [
                    "resume",
                    "--provider", "local",
                    "--protocol", "chat_completions",
                    "--base-url", "https://example.test/v1",
                    "--model", "fake-model"
                ];
            var exitCode = await CliRunner.RunAsync(
                resumeArguments,
                httpClient,
                new TestSessionStore(store),
                TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            Assert.Contains($"Resumed session: {created.Id}", console.Output.ToString());
            var events = File.ReadAllLines(created.FilePath);
            Assert.Equal(1, events.Count(static line => line.Contains("\"type\":\"header\"", StringComparison.Ordinal)));
            Assert.Equal(2, events.Count(static line => line.Contains("\"event\":\"turn_started\"", StringComparison.Ordinal)));
            Assert.Contains(events, static line => line.Contains("\"content\":\"next\"", StringComparison.Ordinal));
        }
        finally
        {
            created?.Dispose();
            Directory.Delete(sessions.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task ResumeReportsAndFallsBackWhenRecordedProfileIsMissing()
    {
        using var httpClient = CreateHttpClient();
        using var console = new ConsoleCapture("/exit\n");
        var sessions = Directory.CreateTempSubdirectory("wfx-cli-tests-");
        var store = new SessionStore(sessions.FullName);
        var workspace = WorkspaceInfo.Discover().Root;
        SessionLog? created = null;
        const string profile = "resume-profile-that-does-not-exist";
        try
        {
            created = store.Create(workspace);
            var recorder = new SessionRecorder(created);
            await recorder.OnTurnStartedAsync(
                new EndpointIdentity(profile, "local", "chat_completions", "fake-model"),
                CancellationToken.None);
            await recorder.OnMessageAsync(new ModelMessage(ModelRole.User, "previous"), CancellationToken.None);
            created.Dispose();

            var exitCode = await CliRunner.RunAsync(
                [
                    "resume",
                    "--id", created.Id,
                    "--provider", "local",
                    "--protocol", "chat_completions",
                    "--base-url", "https://example.test/v1",
                    "--model", "fake-model"
                ],
                httpClient,
                new TestSessionStore(store),
                TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            Assert.Contains(
                $"wfx: recorded profile '{profile}' is no longer configured; using current settings instead.",
                console.Error.ToString());
        }
        finally
        {
            created?.Dispose();
            Directory.Delete(sessions.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task ResumeResolvesRecordedProfileAndRestoresItsEndpointIdentity()
    {
        using var httpClient = CreateHttpClient();
        using var console = new ConsoleCapture("next\n/exit\n");
        var userProfile = Directory.CreateTempSubdirectory("wfx-cli-profile-");
        var configDirectory = Directory.CreateDirectory(Path.Combine(userProfile.FullName, ".wfx"));
        File.WriteAllText(
            Path.Combine(configDirectory.FullName, "config.json"),
            """
            {
              "profiles": {
                "recorded": {
                  "provider": "local",
                  "protocol": "chat_completions",
                  "base_url": "https://recorded.example/v1",
                  "model": "configured-profile-model"
                }
              }
            }
            """);
        var sessions = Directory.CreateTempSubdirectory("wfx-cli-tests-");
        var store = new SessionStore(sessions.FullName);
        var workspace = WorkspaceInfo.Discover().Root;
        SessionLog? created = null;
        try
        {
            created = store.Create(workspace);
            var recorder = new SessionRecorder(created);
            await recorder.OnTurnStartedAsync(
                new EndpointIdentity("recorded", "local", "chat_completions", "recorded-model"),
                CancellationToken.None);
            await recorder.OnMessageAsync(new ModelMessage(ModelRole.User, "previous"), CancellationToken.None);
            created.Dispose();

            var exitCode = await CliRunner.RunAsync(
                ["resume", "--id", created.Id],
                httpClient,
                new TestSessionStore(store),
                TestContext.Current.CancellationToken,
                userProfile.FullName);

            Assert.Equal(0, exitCode);
            var turnLines = File.ReadAllLines(created.FilePath)
                .Where(static line => line.Contains("\"event\":\"turn_started\"", StringComparison.Ordinal))
                .ToArray();
            using var turn = JsonDocument.Parse(turnLines[^1]);
            var endpoint = turn.RootElement.GetProperty("endpoint");
            Assert.Equal("recorded", endpoint.GetProperty("profile").GetString());
            Assert.Equal("local", endpoint.GetProperty("provider").GetString());
            Assert.Equal("chat_completions", endpoint.GetProperty("protocol").GetString());
            Assert.Equal("recorded-model", endpoint.GetProperty("model").GetString());
        }
        finally
        {
            created?.Dispose();
            Directory.Delete(sessions.FullName, recursive: true);
            Directory.Delete(userProfile.FullName, recursive: true);
        }
    }

    [Theory]
    [InlineData("other", "other-model", true)]
    [InlineData("RECORDED", "recorded-model", false)]
    public async Task ResumeExplicitProfileOnlyReportsWhenItOverridesRecordedEndpoint(
        string profile,
        string expectedModel,
        bool expectOverrideNotice)
    {
        using var httpClient = CreateHttpClient();
        using var console = new ConsoleCapture("next\n/exit\n");
        var userProfile = Directory.CreateTempSubdirectory("wfx-cli-profile-");
        var configDirectory = Directory.CreateDirectory(Path.Combine(userProfile.FullName, ".wfx"));
        File.WriteAllText(
            Path.Combine(configDirectory.FullName, "config.json"),
            """
            {
              "profiles": {
                "recorded": {
                  "provider": "local",
                  "protocol": "chat_completions",
                  "base_url": "https://recorded.example/v1",
                  "model": "configured-recorded-model"
                },
                "other": {
                  "provider": "local",
                  "protocol": "chat_completions",
                  "base_url": "https://other.example/v1",
                  "model": "other-model"
                }
              }
            }
            """);
        var sessions = Directory.CreateTempSubdirectory("wfx-cli-tests-");
        var store = new SessionStore(sessions.FullName);
        var workspace = WorkspaceInfo.Discover().Root;
        SessionLog? created = null;
        try
        {
            created = store.Create(workspace);
            var recorder = new SessionRecorder(created);
            await recorder.OnTurnStartedAsync(
                new EndpointIdentity("recorded", "local", "chat_completions", "recorded-model"),
                CancellationToken.None);
            await recorder.OnMessageAsync(new ModelMessage(ModelRole.User, "previous"), CancellationToken.None);
            created.Dispose();

            var exitCode = await CliRunner.RunAsync(
                ["resume", "--id", created.Id, "--profile", profile],
                httpClient,
                new TestSessionStore(store),
                TestContext.Current.CancellationToken,
                userProfile.FullName);

            Assert.Equal(0, exitCode);
            if (expectOverrideNotice)
            {
                Assert.Contains(
                    $"wfx: profile '{profile}' overrides the recorded endpoint for this resumed session.",
                    console.Error.ToString());
            }
            else
            {
                Assert.DoesNotContain("overrides the recorded endpoint", console.Error.ToString());
            }

            var turnLines = File.ReadAllLines(created.FilePath)
                .Where(static line => line.Contains("\"event\":\"turn_started\"", StringComparison.Ordinal))
                .ToArray();
            using var turn = JsonDocument.Parse(turnLines[^1]);
            var endpoint = turn.RootElement.GetProperty("endpoint");
            Assert.Equal(
                expectOverrideNotice ? profile : "recorded",
                endpoint.GetProperty("profile").GetString());
            Assert.Equal("local", endpoint.GetProperty("provider").GetString());
            Assert.Equal("chat_completions", endpoint.GetProperty("protocol").GetString());
            Assert.Equal(expectedModel, endpoint.GetProperty("model").GetString());
        }
        finally
        {
            created?.Dispose();
            Directory.Delete(sessions.FullName, recursive: true);
            Directory.Delete(userProfile.FullName, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, "recorded-model")]
    [InlineData(true, "explicit-model")]
    public async Task ResumeRestoresRecordedEndpointIdentityUnlessExplicitModelWins(
        bool useExplicitModel,
        string expectedModel)
    {
        using var httpClient = CreateHttpClient();
        using var console = new ConsoleCapture("next\n/exit\n");
        var sessions = Directory.CreateTempSubdirectory("wfx-cli-tests-");
        var store = new SessionStore(sessions.FullName);
        var workspace = WorkspaceInfo.Discover().Root;
        SessionLog? created = null;
        try
        {
            created = store.Create(workspace);
            var recorder = new SessionRecorder(created);
            await recorder.OnTurnStartedAsync(
                new EndpointIdentity(null, "openai", "chat_completions", "recorded-model"),
                CancellationToken.None);
            await recorder.OnMessageAsync(new ModelMessage(ModelRole.User, "previous"), CancellationToken.None);
            created.Dispose();

            string[] resumeArguments = useExplicitModel
                ? ["resume", "--id", created.Id, "--model", "explicit-model"]
                : ["resume", "--id", created.Id];
            var exitCode = await CliRunner.RunAsync(
                resumeArguments,
                httpClient,
                new TestSessionStore(store),
                TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            var turnLines = File.ReadAllLines(created.FilePath)
                .Where(static line => line.Contains("\"event\":\"turn_started\"", StringComparison.Ordinal))
                .ToArray();
            using var turn = JsonDocument.Parse(turnLines[^1]);
            var endpoint = turn.RootElement.GetProperty("endpoint");
            Assert.Equal("openai", endpoint.GetProperty("provider").GetString());
            Assert.Equal("chat_completions", endpoint.GetProperty("protocol").GetString());
            Assert.Equal(expectedModel, endpoint.GetProperty("model").GetString());
        }
        finally
        {
            created?.Dispose();
            Directory.Delete(sessions.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task HelpDocumentsResumeAndSessionId()
    {
        using var httpClient = CreateHttpClient();
        using var console = new ConsoleCapture();

        var exitCode = await CliRunner.RunAsync(
            ["--help"],
            httpClient,
            new TestSessionStore(new SessionStore()),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("wfx resume", console.Output.ToString());
        Assert.Contains("--id <session-id>", console.Output.ToString());
        Assert.Contains("--force", console.Output.ToString());
    }

    [Fact]
    public async Task InteractiveHelpDocumentsTheResumeEntryPoint()
    {
        using var httpClient = CreateHttpClient();
        using var console = new ConsoleCapture("/help\n/exit\n");

        var exitCode = await CliRunner.RunAsync(
            [
                "--provider", "local",
                "--protocol", "chat_completions",
                "--base-url", "https://example.test/v1",
                "--model", "fake-model",
                "--no-session"
            ],
            httpClient,
            new TestSessionStore(new SessionStore()),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("wfx resume", console.Output.ToString());
    }

    [Fact]
    public async Task RunWarnsWhenApprovalIsYolo()
    {
        using var httpClient = CreateHttpClient();
        using var console = new ConsoleCapture();
        var exitCode = await CliRunner.RunAsync(
            [
                "run",
                "--provider", "local",
                "--protocol", "chat_completions",
                "--base-url", "https://example.test/v1",
                "--model", "fake-model",
                "--approval", "yolo",
                "--no-session",
                "do it"
            ],
            httpClient,
            new TestSessionStore(new SessionStore()),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("approval is yolo", console.ErrorText);
        Assert.Contains("Workspace path checks still apply", console.ErrorText);
    }

    [Fact]
    public void SessionIdIsOnlyAcceptedByResume()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => CliArguments.Parse(["run", "--id", "session-1", "prompt"]));

        Assert.Contains("--id is only valid with 'wfx resume'", exception.Message);
    }

    [Fact]
    public void ResumeCannotDisableSessionPersistence()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => CliArguments.Parse(["resume", "--no-session"]));

        Assert.Contains("'resume' cannot be combined with --no-session", exception.Message);
    }

    [Fact]
    public void ForceRequiresResumeWithAnExplicitSessionId()
    {
        var wrongCommand = Assert.Throws<ArgumentException>(
            () => CliArguments.Parse(["run", "--force", "prompt"]));
        var missingId = Assert.Throws<ArgumentException>(
            () => CliArguments.Parse(["resume", "--force"]));

        Assert.Contains("--force is only valid with 'wfx resume'", wrongCommand.Message);
        Assert.Contains("--force requires --id", missingId.Message);
        Assert.True(CliArguments.Parse(["resume", "--id", "session-1", "--force"]).Force);
    }

    private static readonly string[] RunArguments =
    [
        "run",
        "--provider", "local",
        "--protocol", "chat_completions",
        "--base-url", "https://example.test/v1",
        "--model", "fake-model",
        "do it"
    ];

    private static readonly string[] InteractiveArguments =
    [
        "--provider", "local",
        "--protocol", "chat_completions",
        "--base-url", "https://example.test/v1",
        "--model", "fake-model"
    ];

    private static readonly string[] NoSessionArguments =
    [
        "run",
        "--provider", "local",
        "--protocol", "chat_completions",
        "--base-url", "https://example.test/v1",
        "--model", "fake-model",
        "--no-session",
        "do it"
    ];

    private static HttpClient CreateHttpClient() => CliRunner.CreateCompletedHttpClient();

    private sealed class QueuedModelProvider(IReadOnlyList<ModelCompleted> responses) : IModelProvider
    {
        public int RequestCount { get; private set; }

        public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return responses[RequestCount++];
        }
    }

    private sealed class SessionAnnouncementFailingWriter : TextWriter
    {
        private readonly StringWriter _written = new();
        private bool _failed;

        public override Encoding Encoding => Encoding.UTF8;

        public string Text => _written.ToString();

        public override void WriteLine(string? value)
        {
            if (!_failed && value?.StartsWith("wfx: session ", StringComparison.Ordinal) == true)
            {
                _failed = true;
                throw new IOException("session announcement failed");
            }

            _written.WriteLine(value);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _written.Dispose();
            }

            base.Dispose(disposing);
        }
    }

}
