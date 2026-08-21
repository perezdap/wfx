using System.Text.Json;
using Wfx.Core;
using Wfx.PowerShell;
using Wfx.Tools;

namespace Wfx.Tools.Tests;

public sealed class GitToolTests
{
    [Fact]
    public async Task PrefixesReadOnlyOperationsWithPagerAndHookLocks()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var workspace = new TemporaryDirectory();
        var executor = new CapturingExecutor();
        var tool = new GitTool(new WorkspacePathPolicy(workspace.Path), executor);
        using var arguments = JsonDocument.Parse("""{ "operation": "status" }""");

        var result = await tool.ExecuteAsync(arguments.RootElement, new ToolContext(workspace.Path), cancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.NotNull(executor.Last);
        var args = executor.Last!.Arguments;
        Assert.Equal("--no-pager", args[0]);
        Assert.Equal("-c", args[1]);
        var expectedHooks = OperatingSystem.IsWindows() ? "core.hooksPath=NUL" : "core.hooksPath=/dev/null";
        Assert.Equal(expectedHooks, args[2]);
        Assert.Equal("-c", args[3]);
        Assert.Equal("core.fsmonitor=", args[4]);
        Assert.Equal("status", args[5]);
    }

    private sealed class CapturingExecutor : IProcessExecutor
    {
        public ProcessCommand? Last { get; private set; }

        public Task<ProcessExecutionResult> ExecuteAsync(
            ProcessCommand command,
            CancellationToken cancellationToken = default)
        {
            Last = command;
            return Task.FromResult(new ProcessExecutionResult("ok", "", 0, false, TimeSpan.Zero));
        }
    }
}
