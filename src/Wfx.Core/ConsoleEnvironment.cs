namespace Wfx.Core;

/// <summary>
/// How this process's console is attached: which of the standard streams are redirected
/// rather than bound to a terminal. Injected so terminal-dependent decisions — the startup
/// approval gate and the human-output split (ADR 0011) above all — are decidable in-process
/// without spawning a shell.
/// </summary>
public interface IConsoleEnvironment
{
    /// <summary>Standard input is redirected, so nobody is at a keyboard to answer a prompt.</summary>
    bool IsInputRedirected { get; }

    /// <summary>Standard output is redirected, so it should carry the final response (ADR 0011).</summary>
    bool IsOutputRedirected { get; }

    /// <summary>Standard error is redirected, so decoration would land in a file or pipe.</summary>
    bool IsErrorRedirected { get; }
}
