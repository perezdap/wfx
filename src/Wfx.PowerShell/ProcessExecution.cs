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

/// <summary>
/// One long-lived child process with redirected UTF-8 stdio, for interactive transports such
/// as the MCP stdio client. Disposal applies the same discipline as <see cref="ProcessExecutor"/>
/// cancellation: kill the entire process tree, wait for exit, then release the handles.
/// </summary>
public sealed class ChildProcessSession : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task _exitTask;
    private bool _disposed;

    internal ChildProcessSession(Process process)
    {
        _process = process;
        StandardInput = process.StandardInput;
        StandardOutput = process.StandardOutput;
        StandardError = process.StandardError;
        _exitTask = WaitForExitQuietAsync(process);
    }

    public StreamWriter StandardInput { get; }

    public StreamReader StandardOutput { get; }

    public StreamReader StandardError { get; }

    /// <summary>
    /// Completes when the process exits, whether it ends on its own (stdin closed, work done)
    /// or is killed by disposal.
    /// </summary>
    public Task Exited => _exitTask;

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The process exited between the check and Kill.
        }

        await _exitTask.ConfigureAwait(false);
        _process.Dispose();
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
}

public sealed class ProcessExecutor : IProcessExecutor
{
    internal const int MaxCapturedCharacters = 1_048_576;

    /// <summary>
    /// Starts a long-lived child process with redirected stdin/stdout/stderr and the same
    /// validation and secret-scrubbed environment as <see cref="ExecuteAsync"/>. Stdin uses
    /// BOM-less UTF-8 because interactive transports such as MCP expect it. The caller owns
    /// the returned session and disposes it to kill the process tree.
    /// </summary>
    public ChildProcessSession StartSession(ProcessCommand command)
    {
        var startInfo = BuildStartInfo(command, redirectStandardInput: true);
        startInfo.StandardInputEncoding = Utf8NoBom;
        var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Failed to start process '{command.FileName}'.");
            }
        }
        catch
        {
            process.Dispose();
            throw;
        }

        return new ChildProcessSession(process);
    }

    public async Task<ProcessExecutionResult> ExecuteAsync(
        ProcessCommand command,
        CancellationToken cancellationToken = default)
    {
        var startInfo = BuildStartInfo(command, redirectStandardInput: command.StandardInput is not null);

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

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static ProcessStartInfo BuildStartInfo(ProcessCommand command, bool redirectStandardInput)
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
            RedirectStandardInput = redirectStandardInput,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        ChildProcessEnvironment.Apply(startInfo.Environment, command.Environment);
        return startInfo;
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
