using System.Net;
using System.Text;
using Wfx.Core;

namespace Wfx.Cli.Tests;

/// <summary>
/// Drives <see cref="Program.RunAsync"/> at the CLI seam. Tests that script stdin through
/// <see cref="ConsoleCapture"/> stand in for a human at a terminal, so the console environment
/// defaults to a terminal; tests about the startup approval gate pass their own.
/// </summary>
internal static class CliRunner
{
    public static HttpClient CreateCompletedHttpClient() => new(new CompletedTurnHandler())
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    public static HttpClient CreateUnexpectedHttpClient(string message) =>
        new(new UnexpectedRequestHandler(message))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

    public static Task<int> RunAsync(
        string[] args,
        HttpClient httpClient,
        ISessionStore sessionStore,
        CancellationToken cancellationToken,
        string? userProfile = null,
        IConsoleEnvironment? consoleEnvironment = null,
        Func<WfxSettings, HttpClient, IModelProvider>? modelProviderFactory = null) =>
        Program.RunAsync(
            args,
            httpClient,
            sessionStore,
            cancellationToken,
            userProfile,
            consoleEnvironment ?? FakeConsoleEnvironment.Terminal,
            modelProviderFactory);

    private sealed class UnexpectedRequestHandler(string message) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(message);
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
