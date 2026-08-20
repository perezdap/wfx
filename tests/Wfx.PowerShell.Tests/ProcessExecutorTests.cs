using Wfx.PowerShell;

namespace Wfx.PowerShell.Tests;

public sealed class ProcessExecutorTests
{
    [Fact]
    public async Task CapturesOutputAndExitCode()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var executor = new ProcessExecutor();
        var result = await executor.ExecuteAsync(new ProcessCommand(
            "cmd.exe",
            ["/d", "/c", "echo hello"],
            Environment.CurrentDirectory,
            Timeout: TimeSpan.FromSeconds(10)), TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello", result.StandardOutput);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task TerminatesProcessOnTimeout()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var executor = new ProcessExecutor();
        var result = await executor.ExecuteAsync(new ProcessCommand(
            "cmd.exe",
            ["/d", "/c", "ping 127.0.0.1 -n 30 > nul"],
            Environment.CurrentDirectory,
            Timeout: TimeSpan.FromMilliseconds(100)), TestContext.Current.CancellationToken);

        Assert.True(result.TimedOut);
        Assert.Equal(-1, result.ExitCode);
    }

    [Fact]
    public async Task PropagatesCancellationAfterTerminatingProcess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var executor = new ProcessExecutor();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.ExecuteAsync(new ProcessCommand(
            "cmd.exe",
            ["/d", "/c", "ping 127.0.0.1 -n 30 > nul"],
            Environment.CurrentDirectory,
            Timeout: TimeSpan.FromSeconds(30)), cancellation.Token));
    }
}
