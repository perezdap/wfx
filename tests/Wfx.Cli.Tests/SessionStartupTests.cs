using System.Net;
using System.Text;
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
            var exitCode = await Program.RunAsync(
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

        var exitCode = await Program.RunAsync(
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
            var exitCode = await Program.RunAsync(
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
            var exitCode = await Program.RunAsync(
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
        var exitCode = await Program.RunAsync(
            NoSessionArguments,
            httpClient,
            store,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.False(createCalled);
        Assert.DoesNotContain("session ", console.ErrorText, StringComparison.OrdinalIgnoreCase);
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

    private static HttpClient CreateHttpClient() => new(new StubHandler())
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

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

    private sealed class StubHandler : HttpMessageHandler
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
