using System.Diagnostics;
using System.Text;

namespace Wfx.PowerShell;

public sealed record ProcessCommand(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    string? StandardInput = null,
    IReadOnlyDictionary<string, string?>? Environment = null,
    TimeSpan? Timeout = null);

public sealed record ProcessExecutionResult(
    string StandardOutput,
    string StandardError,
    int ExitCode,
    bool TimedOut,
    TimeSpan Duration);

public interface IProcessExecutor
{
    Task<ProcessExecutionResult> ExecuteAsync(
        ProcessCommand command,
        CancellationToken cancellationToken = default);
}

public sealed class ProcessExecutor : IProcessExecutor
{
    public async Task<ProcessExecutionResult> ExecuteAsync(
        ProcessCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.FileName);
        if (!Directory.Exists(command.WorkingDirectory))
        {
            throw new DirectoryNotFoundException(command.WorkingDirectory);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = command.FileName,
            WorkingDirectory = command.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = command.StandardInput is not null,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (command.Environment is not null)
        {
            foreach (var pair in command.Environment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        var started = Stopwatch.GetTimestamp();
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process '{command.FileName}'.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        if (command.StandardInput is not null)
        {
            await process.StandardInput.WriteAsync(command.StandardInput.AsMemory(), cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
        }

        using var timeoutSource = command.Timeout is { } timeout
            ? new CancellationTokenSource(timeout)
            : new CancellationTokenSource();
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutSource.IsCancellationRequested)
        {
            timedOut = true;
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return new ProcessExecutionResult(
            stdout,
            stderr,
            timedOut ? -1 : process.ExitCode,
            timedOut,
            Stopwatch.GetElapsedTime(started));
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the check and Kill.
        }
    }
}
