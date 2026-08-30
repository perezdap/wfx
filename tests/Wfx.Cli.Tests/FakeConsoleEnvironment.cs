using Wfx.Core;

namespace Wfx.Cli.Tests;

internal sealed record FakeConsoleEnvironment(
    bool IsInputRedirected,
    bool IsOutputRedirected,
    bool IsErrorRedirected = false)
    : IConsoleEnvironment
{
    /// <summary>A console attached to a terminal on every end.</summary>
    public static FakeConsoleEnvironment Terminal { get; } =
        new(IsInputRedirected: false, IsOutputRedirected: false);

    /// <summary>A console driven by another program: every standard stream is a pipe.</summary>
    public static FakeConsoleEnvironment Redirected { get; } =
        new(IsInputRedirected: true, IsOutputRedirected: true, IsErrorRedirected: true);

    /// <summary>A terminal with stdout alone redirected, as in <c>wfx run "..." &gt; notes.md</c>.</summary>
    public static FakeConsoleEnvironment OutputRedirected { get; } =
        new(IsInputRedirected: false, IsOutputRedirected: true);
}
