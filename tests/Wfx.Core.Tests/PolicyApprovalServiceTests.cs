using Wfx.Core;

namespace Wfx.Core.Tests;

public sealed class PolicyApprovalServiceTests
{
    [Theory]
    [InlineData(ApprovalMode.Always, ApprovalLevel.ReadOnly, true, false)]
    [InlineData(ApprovalMode.Always, ApprovalLevel.WorkspaceWrite, false, true)]
    [InlineData(ApprovalMode.Always, ApprovalLevel.SystemChange, false, true)]
    [InlineData(ApprovalMode.Always, ApprovalLevel.Dangerous, false, true)]
    [InlineData(ApprovalMode.Workspace, ApprovalLevel.ReadOnly, true, false)]
    [InlineData(ApprovalMode.Workspace, ApprovalLevel.WorkspaceWrite, true, false)]
    [InlineData(ApprovalMode.Workspace, ApprovalLevel.SystemChange, false, true)]
    [InlineData(ApprovalMode.Workspace, ApprovalLevel.Dangerous, false, true)]
    [InlineData(ApprovalMode.Never, ApprovalLevel.ReadOnly, true, false)]
    [InlineData(ApprovalMode.Never, ApprovalLevel.WorkspaceWrite, false, false)]
    [InlineData(ApprovalMode.Never, ApprovalLevel.SystemChange, false, false)]
    [InlineData(ApprovalMode.Never, ApprovalLevel.Dangerous, false, false)]
    [InlineData(ApprovalMode.Yolo, ApprovalLevel.ReadOnly, true, false)]
    [InlineData(ApprovalMode.Yolo, ApprovalLevel.WorkspaceWrite, true, false)]
    [InlineData(ApprovalMode.Yolo, ApprovalLevel.SystemChange, true, false)]
    [InlineData(ApprovalMode.Yolo, ApprovalLevel.Dangerous, true, false)]
    public async Task DecidesFromModeAndOnlyPromptsWhenPolicyDoesNot(
        ApprovalMode mode,
        ApprovalLevel level,
        bool allowed,
        bool prompt)
    {
        var prompted = false;
        var service = new PolicyApprovalService(mode, (_, _) =>
        {
            prompted = true;
            return ValueTask.FromResult(false);
        });

        var approved = await service.ApproveAsync(Request(level), TestContext.Current.CancellationToken);

        Assert.Equal(allowed, approved);
        Assert.Equal(prompt, prompted);
    }

    private static ApprovalRequest Request(ApprovalLevel level) =>
        new("powershell", """{"script":"rm C:\\ -Recurse"}""", level, "Run powershell");
}
