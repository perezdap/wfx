using System.Net;
using System.Text;
using Wfx.Core;

namespace Wfx.Cli.Tests;

public sealed class SessionStartupTests
{
    [Fact]
    public async Task RunContinuesWhenSessionsPathIsAFile()
    {
        var directory = Directory.CreateTempSubdirectory("wfx-cli-tests-");
        var sessionsPath = Path.Combine(directory.FullName, "sessions");
        File.WriteAllText(sessionsPath, "not a directory");
        using var httpClient = CreateHttpClient();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        Console.SetOut(output);
        Console.SetError(error);
        try
        {
            var store = new SessionStore(sessionsPath);
            var exitCode = await Program.RunAsync(
                RunArguments,
                httpClient,
                store.Create,
                TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            Assert.Contains("finished", output.ToString());
            Assert.Contains("wfx: warning: Could not create session:", error.ToString());
            Assert.Contains("The invocation will continue without a session.", error.ToString());
            Assert.True(File.Exists(sessionsPath));
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task InteractiveContinuesWhenSessionCreationIsUnauthorized()
    {
        using var httpClient = CreateHttpClient();
        var originalIn = Console.In;
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var input = new StringReader("do it\n/exit\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        Console.SetIn(input);
        Console.SetOut(output);
        Console.SetError(error);
        try
        {
            var exitCode = await Program.RunAsync(
                InteractiveArguments,
                httpClient,
                _ => throw new UnauthorizedAccessException("session ACL denied"),
                TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            Assert.Contains("finished", output.ToString());
            Assert.Contains(
                "wfx: warning: Could not create session: session ACL denied. The invocation will continue without a session.",
                error.ToString());
        }
        finally
        {
            Console.SetIn(originalIn);
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    [Fact]
    public async Task RunAnnouncesAndWritesSuccessfulSession()
    {
        var directory = Directory.CreateTempSubdirectory("wfx-cli-tests-");
        using var httpClient = CreateHttpClient();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        Console.SetOut(output);
        Console.SetError(error);
        try
        {
            var store = new SessionStore(directory.FullName);
            var exitCode = await Program.RunAsync(
                RunArguments,
                httpClient,
                store.Create,
                TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            Assert.Contains("wfx: session ", error.ToString());
            Assert.DoesNotContain("wfx: warning: Could not create session", error.ToString());
            Assert.Single(Directory.GetFiles(directory.FullName, "*.jsonl"));
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task NoSessionDoesNotCreateOrAnnounceASession()
    {
        using var httpClient = CreateHttpClient();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        Console.SetOut(output);
        Console.SetError(error);
        var createCalled = false;
        try
        {
            var exitCode = await Program.RunAsync(
                NoSessionArguments,
                httpClient,
                _ =>
                {
                    createCalled = true;
                    throw new InvalidOperationException("Session creation must be skipped.");
                },
                TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            Assert.False(createCalled);
            Assert.DoesNotContain("session ", error.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
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
