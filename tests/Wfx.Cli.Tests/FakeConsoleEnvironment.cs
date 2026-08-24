using Wfx.Core;

namespace Wfx.Cli.Tests;

internal sealed record FakeConsoleEnvironment(bool IsInputRedirected, bool IsOutputRedirected)
    : IConsoleEnvironment
{
    /// <summary>A console attached to a terminal on both ends.</summary>
    public static FakeConsoleEnvironment Terminal { get; } =
        new(IsInputRedirected: false, IsOutputRedirected: false);

    /// <summary>A console driven by another program: stdin and stdout are both pipes.</summary>
    public static FakeConsoleEnvironment Redirected { get; } =
        new(IsInputRedirected: true, IsOutputRedirected: true);
}
