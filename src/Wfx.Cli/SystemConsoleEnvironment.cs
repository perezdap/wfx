using Wfx.Core;

namespace Wfx.Cli;

/// <summary>The real console, as the running process sees it.</summary>
internal sealed class SystemConsoleEnvironment : IConsoleEnvironment
{
    public static SystemConsoleEnvironment Instance { get; } = new();

    public bool IsInputRedirected => Console.IsInputRedirected;

    public bool IsOutputRedirected => Console.IsOutputRedirected;

    public bool IsErrorRedirected => Console.IsErrorRedirected;
}
