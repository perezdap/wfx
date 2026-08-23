using Wfx.Core;

namespace Wfx.Cli.Tests;

[Collection("Console")]
public sealed class SessionListingTests
{
    [Fact]
    public async Task SessionsReportsAnEmptyStoreWithoutModelConfiguration()
    {
        using var httpClient = new HttpClient(new UnexpectedRequestHandler());
        using var console = new ConsoleCapture();
        var listCalled = false;

        var exitCode = await Program.RunAsync(
            ["sessions"],
            httpClient,
            _ => throw new InvalidOperationException("Session creation must be skipped."),
            () =>
            {
                listCalled = true;
                return [];
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.True(listCalled);
        Assert.Contains("No sessions.", console.Output.ToString());
        Assert.Contains("Total on disk: 0 B", console.Output.ToString());
        Assert.Empty(console.ErrorText);
    }

    [Fact]
    public async Task SessionsPrintsWorkspaceTimestampsSizeAndTotalFromOneSnapshot()
    {
        using var httpClient = new HttpClient(new UnexpectedRequestHandler());
        using var console = new ConsoleCapture();
        var listCalls = 0;
        IReadOnlyList<SessionSummary> sessions =
        [
            new(
                "20260823T010203Z-abc123",
                @"C:\src\wfx",
                new DateTime(2026, 8, 23, 1, 2, 3, DateTimeKind.Utc),
                new DateTime(2026, 8, 23, 4, 5, 6, DateTimeKind.Utc),
                512)
        ];

        var exitCode = await Program.RunAsync(
            ["sessions"],
            httpClient,
            _ => throw new InvalidOperationException("Session creation must be skipped."),
            () =>
            {
                listCalls++;
                return sessions;
            },
            TestContext.Current.CancellationToken);

        var output = console.Output.ToString();
        Assert.Equal(0, exitCode);
        Assert.Equal(1, listCalls);
        Assert.Contains("SESSION", output);
        Assert.Contains("WORKSPACE", output);
        Assert.Contains("20260823T010203Z-abc123", output);
        Assert.Contains(@"C:\src\wfx", output);
        Assert.Contains("2026-08-23 01:02:03", output);
        Assert.Contains("2026-08-23 04:05:06", output);
        Assert.Contains("512 B", output);
        Assert.Contains("1 session(s), 512 B total on disk", output);
        Assert.Empty(console.ErrorText);
    }

    [Fact]
    public async Task HelpDocumentsTheSessionsCommand()
    {
        using var httpClient = new HttpClient(new UnexpectedRequestHandler());
        using var console = new ConsoleCapture();

        var exitCode = await Program.RunAsync(
            ["--help"],
            httpClient,
            _ => throw new InvalidOperationException("Session creation must be skipped."),
            () => throw new InvalidOperationException("Session listing must be skipped."),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("wfx sessions [options]", console.Output.ToString());
        Assert.Empty(console.ErrorText);
    }

    private sealed class UnexpectedRequestHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The sessions command must not call a model endpoint.");
    }
}
