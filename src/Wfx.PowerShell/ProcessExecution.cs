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
    TimeSpan Duration,
    bool Truncated = false);

public interface IProcessExecutor
{
    Task<ProcessExecutionResult> ExecuteAsync(
        ProcessCommand command,
        CancellationToken cancellationToken = default);
}

public sealed class ProcessExecutor : IProcessExecutor
{
    internal const int MaxCapturedCharacters = 1_048_576;

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

        ChildProcessEnvironment.Apply(startInfo.Environment, command.Environment);

        using var process = new Process { StartInfo = startInfo };
        var started = Stopwatch.GetTimestamp();
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process '{command.FileName}'.");
        }

        // Do not bind pipe reads to the caller token: killing the process closes the
        // pipes, and we always drain before disposing so Ctrl+C cannot race dispose.
        var stdoutTask = ReadBoundedAsync(process.StandardOutput, CancellationToken.None);
        var stderrTask = ReadBoundedAsync(process.StandardError, CancellationToken.None);

        using var timeoutSource = command.Timeout is { } timeout
            ? new CancellationTokenSource(timeout)
            : new CancellationTokenSource();
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);

        var timedOut = false;
        var canceled = false;
        try
        {
            if (command.StandardInput is not null)
            {
                await process.StandardInput.WriteAsync(command.StandardInput.AsMemory(), linkedSource.Token)
                    .ConfigureAwait(false);
                process.StandardInput.Close();
            }

            await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutSource.IsCancellationRequested)
        {
            timedOut = true;
            TryKill(process);
            await WaitForExitQuietAsync(process).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            canceled = true;
            TryKill(process);
            await WaitForExitQuietAsync(process).ConfigureAwait(false);
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (canceled)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        return new ProcessExecutionResult(
            stdout.Text,
            stderr.Text,
            timedOut ? -1 : process.ExitCode,
            timedOut,
            Stopwatch.GetElapsedTime(started),
            stdout.Truncated || stderr.Truncated);
    }

    private static async Task<(string Text, bool Truncated)> ReadBoundedAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var buffer = new char[16 * 1024];
        var truncated = false;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (truncated)
            {
                continue;
            }

            var remaining = MaxCapturedCharacters - builder.Length;
            if (read <= remaining)
            {
                builder.Append(buffer, 0, read);
            }
            else
            {
                if (remaining > 0)
                {
                    builder.Append(buffer, 0, remaining);
                }

                truncated = true;
            }
        }

        return (builder.ToString(), truncated);
    }

    private static async Task WaitForExitQuietAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // The process is already gone.
        }
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
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The process exited between the check and Kill.
        }
    }
}
