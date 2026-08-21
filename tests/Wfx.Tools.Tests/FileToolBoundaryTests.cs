using System.Text.Json;
using Wfx.Core;
using Wfx.PowerShell;
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

    [Theory]
    [InlineData("read_file")]
    [InlineData("list_directory")]
    [InlineData("apply_patch")]
    [InlineData("powershell")]
    public async Task ToolsCannotEscapeWorkspace(string toolName)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var workspace = new TemporaryDirectory();
        var paths = new WorkspacePathPolicy(workspace.Path);
        ITool tool = toolName switch
        {
            "read_file" => new ReadFileTool(paths),
            "list_directory" => new ListDirectoryTool(paths),
            "apply_patch" => new ApplyPatchTool(paths),
            "powershell" => new PowerShellTool(paths, new UnexpectedPowerShellRunner()),
            _ => throw new ArgumentOutOfRangeException(nameof(toolName))
        };
        var argumentsJson = toolName switch
        {
            "read_file" => """{ "path": "../outside.txt" }""",
            "list_directory" => """{ "path": ".." }""",
            "apply_patch" => """{ "path": "../outside.txt", "patch": "@@ -1 +1 @@\n-a\n+b" }""",
            "powershell" => """{ "script": "Get-ChildItem", "working_directory": ".." }""",
            _ => throw new ArgumentOutOfRangeException(nameof(toolName))
        };
        using var arguments = JsonDocument.Parse(argumentsJson);

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

    [Fact]
    public async Task SearchesSkipFileLinksOutsideWorkspace()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var workspace = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "inside.txt"), "needle", cancellationToken);
        var outsideFile = Path.Combine(outside.Path, "secret.txt");
        await File.WriteAllTextAsync(outsideFile, "needle secret", cancellationToken);
        var link = Path.Combine(workspace.Path, "linked-secret.txt");
        try
        {
            File.CreateSymbolicLink(link, outsideFile);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Skip($"Unable to create a file symbolic link: {exception.Message}");
        }

        using var searchFilesArguments = JsonDocument.Parse("""
            { "path": ".", "pattern": "*.txt" }
            """);
        using var searchTextArguments = JsonDocument.Parse("""
            { "path": ".", "query": "needle", "glob": "*.txt" }
            """);
        var paths = new WorkspacePathPolicy(workspace.Path);

        var filesResult = await new SearchFilesTool(paths).ExecuteAsync(
            searchFilesArguments.RootElement,
            new ToolContext(workspace.Path),
            cancellationToken);
        var textResult = await new SearchTextTool(paths).ExecuteAsync(
            searchTextArguments.RootElement,
            new ToolContext(workspace.Path),
            cancellationToken);

        Assert.True(filesResult.Success, filesResult.Error);
        Assert.Equal("inside.txt", filesResult.Output);
        Assert.True(textResult.Success, textResult.Error);
        Assert.Contains("inside.txt:1:needle", textResult.Output);
        Assert.DoesNotContain("linked-secret.txt", textResult.Output);
    }

    [Fact]
    public async Task WriteFileCreatesNestedDirectoriesInsideWorkspace()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var workspace = new TemporaryDirectory();
        var tool = new WriteFileTool(new WorkspacePathPolicy(workspace.Path));
        using var arguments = JsonDocument.Parse("""
            { "path": "nested/dir/file.txt", "content": "ok", "create_directories": true }
            """);

        var result = await tool.ExecuteAsync(arguments.RootElement, new ToolContext(workspace.Path), cancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.Equal("ok", await File.ReadAllTextAsync(Path.Combine(workspace.Path, "nested", "dir", "file.txt"), cancellationToken));
    }

    private sealed class UnexpectedPowerShellRunner : IPowerShellRunner
    {
        public Task<ProcessExecutionResult> ExecuteAsync(
            PowerShellRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("PowerShell runner should not be called for an escaping working directory.");
    }
}
