namespace Wfx.PowerShell;

public sealed record PowerShellRequest(
    string Script,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?>? Environment = null,
    TimeSpan? Timeout = null);

public interface IPowerShellRunner
{
    Task<ProcessExecutionResult> ExecuteAsync(
        PowerShellRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class PowerShellRunner : IPowerShellRunner
{
    private readonly IProcessExecutor _processExecutor;
    private readonly string? _explicitExecutable;

    public PowerShellRunner(IProcessExecutor processExecutor, string? executable = null)
    {
        _processExecutor = processExecutor;
        _explicitExecutable = executable;
    }

    public async Task<ProcessExecutionResult> ExecuteAsync(
        PowerShellRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Script);
        string[] candidates = _explicitExecutable is not null
            ? [_explicitExecutable]
            : OperatingSystem.IsWindows()
                ? new[] { "pwsh.exe", "powershell.exe" }
                : new[] { "pwsh" };

        Exception? lastError = null;
        foreach (var executable in candidates)
        {
            try
            {
                return await _processExecutor.ExecuteAsync(
                    new ProcessCommand(
                        executable,
                        ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "-"],
                        request.WorkingDirectory,
                        request.Script,
                        request.Environment,
                        request.Timeout ?? TimeSpan.FromMinutes(2)),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (System.ComponentModel.Win32Exception exception)
            {
                lastError = exception;
            }
        }

        throw new InvalidOperationException(
            "PowerShell was not found. Install PowerShell 7 (pwsh.exe), or use Windows PowerShell fallback on Windows.",
            lastError);
    }
}
