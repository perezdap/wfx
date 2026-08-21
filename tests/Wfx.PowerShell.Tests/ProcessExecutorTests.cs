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

    [Fact]
    public async Task OmitsInheritedSecretVariablesFromChildEnvironment()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string name = "WFX_API_KEY";
        const string sentinel = "wfx-test-secret-should-not-leak";
        var previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, sentinel);
        try
        {
            var executor = new ProcessExecutor();
            var result = await executor.ExecuteAsync(new ProcessCommand(
                "cmd.exe",
                ["/d", "/c", $"if defined {name} (echo PRESENT) else (echo ABSENT)"],
                Environment.CurrentDirectory,
                Timeout: TimeSpan.FromSeconds(10)), TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("ABSENT", result.StandardOutput);
            Assert.DoesNotContain(sentinel, result.StandardOutput);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }

    [Fact]
    public async Task OverlayRestoresSpecificSecretVariables()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string name = "WFX_API_KEY";
        const string sentinel = "wfx-test-opt-in-secret";
        var executor = new ProcessExecutor();
        var result = await executor.ExecuteAsync(new ProcessCommand(
            "cmd.exe",
            ["/d", "/c", $"if defined {name} (echo PRESENT) else (echo ABSENT)"],
            Environment.CurrentDirectory,
            Environment: new Dictionary<string, string?> { [name] = sentinel },
            Timeout: TimeSpan.FromSeconds(10)), TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("PRESENT", result.StandardOutput);
        Assert.DoesNotContain(sentinel, result.StandardOutput);
    }

    [Fact]
    public async Task StillInheritsNonSecretVariables()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string name = "WFX_TEST_MARKER";
        const string sentinel = "wfx-test-marker-present";
        var previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, sentinel);
        try
        {
            var executor = new ProcessExecutor();
            var result = await executor.ExecuteAsync(new ProcessCommand(
                "cmd.exe",
                ["/d", "/c", $"if defined {name} (echo PRESENT) else (echo ABSENT)"],
                Environment.CurrentDirectory,
                Timeout: TimeSpan.FromSeconds(10)), TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("PRESENT", result.StandardOutput);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }

    [Fact]
    public async Task ChildEnvironmentDefaultsGitPagerToCat()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var executor = new ProcessExecutor();
        var result = await executor.ExecuteAsync(new ProcessCommand(
            "cmd.exe",
            ["/d", "/c", "echo GIT_PAGER=%GIT_PAGER%& echo PAGER=%PAGER%"],
            Environment.CurrentDirectory,
            Timeout: TimeSpan.FromSeconds(10)), TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("GIT_PAGER=cat", result.StandardOutput);
        Assert.Contains("PAGER=cat", result.StandardOutput);
    }

    [Fact]
    public async Task OverlayWinsOverPagerDefaults()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var executor = new ProcessExecutor();
        var result = await executor.ExecuteAsync(new ProcessCommand(
            "cmd.exe",
            ["/d", "/c", "echo GIT_PAGER=%GIT_PAGER%"],
            Environment.CurrentDirectory,
            Environment: new Dictionary<string, string?> { ["GIT_PAGER"] = "custom-git-pager" },
            Timeout: TimeSpan.FromSeconds(10)), TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("GIT_PAGER=custom-git-pager", result.StandardOutput);
    }
}
