using Wfx.PowerShell;

namespace Wfx.PowerShell.Tests;

public sealed class SyncWfxProfilesScriptTests
{
    [Fact]
    public async Task ListsOllamaAsNoAuthLocalProvider()
    {
        var repositoryRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(repositoryRoot, "tools", "Sync-WfxProfiles.ps1");
        var executor = new ProcessExecutor();

        var result = await executor.ExecuteAsync(new ProcessCommand(
            "pwsh.exe",
            ["-NoLogo", "-NoProfile", "-File", scriptPath, "-ListProviders"],
            repositoryRoot,
            Timeout: TimeSpan.FromSeconds(15)), TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        var ollamaRow = result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.TrimStart().StartsWith("ollama ", StringComparison.Ordinal));
        Assert.Contains("http://127.0.0.1:11434/v1", ollamaRow);
        Assert.Contains("(none)", ollamaRow);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Wfx.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
