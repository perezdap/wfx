using Wfx.Core;

namespace Wfx.Cli.Tests;

public sealed class ApprovalArgumentsTests
{
    [Theory]
    [InlineData("--approval", "yolo")]
    [InlineData("--yolo")]
    public void ParsesYolo(params string[] approvalArgs)
    {
        var arguments = CliArguments.Parse(["run", ..approvalArgs, "do it"]);

        Assert.Equal(ApprovalMode.Yolo, arguments.Settings.Approval);
    }

    [Fact]
    public void AllowsRepeatedYoloFlags()
    {
        var arguments = CliArguments.Parse(["run", "--yolo", "--approval", "yolo", "do it"]);

        Assert.Equal(ApprovalMode.Yolo, arguments.Settings.Approval);
    }

    [Fact]
    public void RejectsConflictingApprovalFlags()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => CliArguments.Parse(["run", "--yolo", "--approval", "workspace", "do it"]));

        Assert.Contains("different values", exception.Message);
    }

    [Fact]
    public void RejectsUnknownApprovalMode()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => CliArguments.Parse(["run", "--approval", "sometimes", "do it"]));

        Assert.Contains("always, workspace, never, or yolo", exception.Message);
    }
}
