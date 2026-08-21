using System.Text.Json;
using Wfx.Core;
using Wfx.PowerShell;
using Wfx.Tools;

namespace Wfx.Tools.Tests;

public sealed class PowerShellToolTests
{
    [Fact]
    public void InheritEnvironmentBumpsReadOnlyScriptToSystemChange()
    {
        using var workspace = new TemporaryDirectory();
        var tool = new PowerShellTool(new WorkspacePathPolicy(workspace.Path), new CapturingRunner());
        using var arguments = JsonDocument.Parse("""
            { "script": "Get-ChildItem", "inherit_environment": ["WFX_API_KEY"] }
            """);

        Assert.Equal(ApprovalLevel.SystemChange, tool.Classify(arguments.RootElement));
    }

    [Fact]
    public void MissingInheritEnvironmentKeepsReadOnlyScriptReadOnly()
    {
        using var workspace = new TemporaryDirectory();
        var tool = new PowerShellTool(new WorkspacePathPolicy(workspace.Path), new CapturingRunner());
        using var arguments = JsonDocument.Parse("""{ "script": "Get-ChildItem" }""");

        Assert.Equal(ApprovalLevel.ReadOnly, tool.Classify(arguments.RootElement));
    }

    [Fact]
    public void InheritEnvironmentDoesNotDowngradeDangerousScripts()
    {
        using var workspace = new TemporaryDirectory();
        var tool = new PowerShellTool(new WorkspacePathPolicy(workspace.Path), new CapturingRunner());
        using var arguments = JsonDocument.Parse("""
            { "script": "Remove-Item C:\\ -Recurse -Force", "inherit_environment": ["WFX_API_KEY"] }
            """);

        Assert.Equal(ApprovalLevel.Dangerous, tool.Classify(arguments.RootElement));
    }

    [Fact]
    public void RejectsNonArrayInheritEnvironment()
    {
        using var workspace = new TemporaryDirectory();
        var tool = new PowerShellTool(new WorkspacePathPolicy(workspace.Path), new CapturingRunner());
        using var arguments = JsonDocument.Parse("""{ "script": "Get-ChildItem", "inherit_environment": "WFX_API_KEY" }""");

        Assert.Throws<JsonException>(() => tool.Classify(arguments.RootElement));
    }

    [Fact]
    public async Task CopiesRequestedParentVariablesIntoChildEnvironment()
    {
        const string name = "WFX_TEST_INHERIT_API_KEY";
        const string sentinel = "wfx-test-inherit-sentinel";
        var previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, sentinel);
        try
        {
            using var workspace = new TemporaryDirectory();
            var runner = new CapturingRunner();
            var tool = new PowerShellTool(new WorkspacePathPolicy(workspace.Path), runner);
            using var arguments = JsonDocument.Parse($$"""
                { "script": "Get-ChildItem", "inherit_environment": ["{{name}}"] }
                """);

            var result = await tool.ExecuteAsync(
                arguments.RootElement,
                new ToolContext(workspace.Path),
                TestContext.Current.CancellationToken);

            Assert.True(result.Success, result.Error);
            Assert.NotNull(runner.Last);
            Assert.NotNull(runner.Last!.Environment);
            Assert.Equal(sentinel, runner.Last.Environment[name]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }

    [Fact]
    public async Task DoesNotCopySecretsThatWereNotRequested()
    {
        const string requested = "WFX_TEST_INHERIT_API_KEY";
        const string other = "WFX_TEST_OTHER_API_KEY";
        var previousRequested = Environment.GetEnvironmentVariable(requested);
        var previousOther = Environment.GetEnvironmentVariable(other);
        Environment.SetEnvironmentVariable(requested, "wfx-test-inherit-sentinel");
        Environment.SetEnvironmentVariable(other, "wfx-test-other-sentinel");
        try
        {
            using var workspace = new TemporaryDirectory();
            var runner = new CapturingRunner();
            var tool = new PowerShellTool(new WorkspacePathPolicy(workspace.Path), runner);
            using var arguments = JsonDocument.Parse($$"""
                { "script": "Get-ChildItem", "inherit_environment": ["{{requested}}"] }
                """);

            await tool.ExecuteAsync(
                arguments.RootElement,
                new ToolContext(workspace.Path),
                TestContext.Current.CancellationToken);

            Assert.NotNull(runner.Last?.Environment);
            Assert.True(runner.Last!.Environment.ContainsKey(requested));
            Assert.False(runner.Last.Environment.ContainsKey(other));
        }
        finally
        {
            Environment.SetEnvironmentVariable(requested, previousRequested);
            Environment.SetEnvironmentVariable(other, previousOther);
        }
    }

    private sealed class CapturingRunner : IPowerShellRunner
    {
        public PowerShellRequest? Last { get; private set; }

        public Task<ProcessExecutionResult> ExecuteAsync(
            PowerShellRequest request,
            CancellationToken cancellationToken = default)
        {
            Last = request;
            return Task.FromResult(new ProcessExecutionResult("ok", "", 0, false, TimeSpan.Zero));
        }
    }
}
