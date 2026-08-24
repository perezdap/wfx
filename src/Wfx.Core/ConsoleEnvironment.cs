namespace Wfx.Core;

/// <summary>
/// How this process's console is attached: whether standard input and standard output are
/// redirected rather than bound to a terminal. Injected so terminal-dependent decisions —
/// the startup approval gate above all — are decidable in-process without spawning a shell.
/// </summary>
public interface IConsoleEnvironment
{
    /// <summary>Standard input is redirected, so nobody is at a keyboard to answer a prompt.</summary>
    bool IsInputRedirected { get; }

    /// <summary>Standard output is redirected, so terminal decoration would land in a file or pipe.</summary>
    bool IsOutputRedirected { get; }
}
