using Wfx.Core;

namespace Wfx.Cli.Tests;

/// <summary>
/// Drives <see cref="Program.RunAsync"/> at the CLI seam. Tests that script stdin through
/// <see cref="ConsoleCapture"/> stand in for a human at a terminal, so the console environment
/// defaults to a terminal; tests about the startup approval gate pass their own.
/// </summary>
internal static class CliRunner
{
    public static Task<int> RunAsync(
        string[] args,
        HttpClient httpClient,
        ISessionStore sessionStore,
        CancellationToken cancellationToken,
        string? userProfile = null,
        IConsoleEnvironment? consoleEnvironment = null) =>
        Program.RunAsync(
            args,
            httpClient,
            sessionStore,
            cancellationToken,
            userProfile,
            consoleEnvironment ?? FakeConsoleEnvironment.Terminal);
}
