using System.Text.Json;
using Wfx.Core;
using Wfx.Tools;

namespace Wfx.Tools.Tests;

public sealed class FileToolBoundaryTests
{
    [Fact]
    public async Task WriteFileCannotEscapeWorkspace()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var workspace = new TemporaryDirectory();
        var tool = new WriteFileTool(new WorkspacePathPolicy(workspace.Path));
        using var arguments = JsonDocument.Parse("""
            { "path": "../outside.txt", "content": "no" }
            """);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await tool.ExecuteAsync(arguments.RootElement, new ToolContext(workspace.Path), cancellationToken));
    }

    [Fact]
    public async Task SearchTextReturnsLineNumbers()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var workspace = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(workspace.Path, "src"));
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "src", "a.cs"), "first\nneedle here\n", cancellationToken);
        var tool = new SearchTextTool(new WorkspacePathPolicy(workspace.Path));
        using var arguments = JsonDocument.Parse("""
            { "path": "src", "query": "needle", "glob": "*.cs" }
            """);

        var result = await tool.ExecuteAsync(arguments.RootElement, new ToolContext(workspace.Path), cancellationToken);

        Assert.True(result.Success);
        Assert.Contains("src/a.cs:2:needle here", result.Output);
    }
}
