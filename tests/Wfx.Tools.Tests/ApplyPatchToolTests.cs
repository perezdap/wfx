using System.Text.Json;
using Wfx.Core;
using Wfx.Tools;

namespace Wfx.Tools.Tests;

public sealed class ApplyPatchToolTests
{
    [Fact]
    public async Task AppliesMatchingUnifiedDiff()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var workspace = new TemporaryDirectory();
        var path = Path.Combine(workspace.Path, "sample.txt");
        await File.WriteAllTextAsync(path, "one\ntwo\nthree\n", cancellationToken);
        var tool = new ApplyPatchTool(new WorkspacePathPolicy(workspace.Path));
        using var arguments = JsonDocument.Parse("""
            {
              "path": "sample.txt",
              "patch": "@@ -1,3 +1,3 @@\n one\n-two\n+TWO\n three"
            }
            """);

        var result = await tool.ExecuteAsync(arguments.RootElement, new ToolContext(workspace.Path), cancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.Equal("one\nTWO\nthree\n", (await File.ReadAllTextAsync(path, cancellationToken)).Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task RejectsMismatchedContextWithoutChangingFile()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var workspace = new TemporaryDirectory();
        var path = Path.Combine(workspace.Path, "sample.txt");
        await File.WriteAllTextAsync(path, "actual\n", cancellationToken);
        var tool = new ApplyPatchTool(new WorkspacePathPolicy(workspace.Path));
        using var arguments = JsonDocument.Parse("""
            { "path": "sample.txt", "patch": "@@ -1 +1 @@\n-expected\n+changed" }
            """);

        var result = await tool.ExecuteAsync(arguments.RootElement, new ToolContext(workspace.Path), cancellationToken);

        Assert.False(result.Success);
        Assert.Equal("actual\n", (await File.ReadAllTextAsync(path, cancellationToken)).Replace("\r\n", "\n"));
    }
}
