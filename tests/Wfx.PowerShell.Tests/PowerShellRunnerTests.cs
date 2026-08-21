using Wfx.PowerShell;

namespace Wfx.PowerShell.Tests;

public sealed class PowerShellRunnerTests
{
    [Fact]
    public async Task SendsScriptThroughStandardInput()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var runner = new PowerShellRunner(new ProcessExecutor());
        var result = await runner.ExecuteAsync(new PowerShellRequest(
            "'wfx-powershell-ok'",
            Environment.CurrentDirectory,
            Timeout: TimeSpan.FromSeconds(15)), TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("wfx-powershell-ok", result.StandardOutput);
    }
}
