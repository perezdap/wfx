using Wfx.Core;
using Wfx.Tools;

namespace Wfx.Tools.Tests;

public sealed class ApprovalClassificationTests
{
    [Theory]
    [InlineData("Get-ChildItem -Recurse", ApprovalLevel.ReadOnly)]
    [InlineData("dotnet test", ApprovalLevel.WorkspaceWrite)]
    [InlineData("Set-Content ./src/file.cs 'x'", ApprovalLevel.WorkspaceWrite)]
    [InlineData("winget install Git.Git", ApprovalLevel.SystemChange)]
    [InlineData("Remove-Item C:\\ -Recurse -Force", ApprovalLevel.Dangerous)]
    [InlineData("& ./unknown.exe", ApprovalLevel.SystemChange)]
    public void ClassifiesPowerShellConservatively(string script, ApprovalLevel expected)
    {
        Assert.Equal(expected, PowerShellTool.ClassifyScript(script));
    }
}
