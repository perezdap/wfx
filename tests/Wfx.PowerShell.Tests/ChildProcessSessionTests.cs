using Wfx.PowerShell;

namespace Wfx.PowerShell.Tests;

public sealed class ChildProcessSessionTests
{
    [Fact]
    public async Task RoundTripsStdioLines()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var executor = new ProcessExecutor();
        await using var session = executor.StartSession(new ProcessCommand(
            "findstr.exe",
            ["^"],
            Environment.CurrentDirectory));

        await session.StandardInput.WriteLineAsync("hello session".AsMemory(), TestContext.Current.CancellationToken);
        await session.StandardInput.FlushAsync(TestContext.Current.CancellationToken);

        // findstr emits on EOF, so close stdin first and then read the echoed line.
        session.StandardInput.Close();

        var echoed = await session.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        Assert.Equal("hello session", echoed);

        await session.Exited.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DisposeKillsARunningProcessTree()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var executor = new ProcessExecutor();
        var session = executor.StartSession(new ProcessCommand(
            "cmd.exe",
            ["/d", "/c", "ping 127.0.0.1 -n 120 > nul"],
            Environment.CurrentDirectory));

        await session.DisposeAsync();

        // Kill propagates to the whole process tree and disposal waits for exit.
        await session.Exited.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
    }

    [Fact]
    public void StartSession_ThrowsForAMissingCommand()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var executor = new ProcessExecutor();
        Assert.Throws<System.ComponentModel.Win32Exception>(() => executor.StartSession(new ProcessCommand(
            "no-such-command-wfx.exe",
            [],
            Environment.CurrentDirectory)));
    }
}
