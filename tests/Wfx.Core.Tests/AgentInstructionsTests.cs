using Wfx.Core;

namespace Wfx.Core.Tests;

public sealed class AgentInstructionsTests
{
    [Fact]
    public async Task DiscoversInstructionsFromRootToWorkingDirectory()
    {
        using var workspace = new TemporaryDirectory();
        var nested = Path.Combine(workspace.Path, "src", "feature");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(workspace.Path, "AGENTS.md"), "root rules");
        File.WriteAllText(Path.Combine(workspace.Path, "src", "AGENTS.md"), "src rules");

        var provider = new AgentInstructionsContextProvider(workspace.Path, nested);
        var result = await provider.GetContextAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Contains("root rules", result);
        Assert.Contains("src rules", result);
        Assert.True(result.IndexOf("root rules", StringComparison.Ordinal) < result.IndexOf("src rules", StringComparison.Ordinal));
    }
}
