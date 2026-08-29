using System.Text.Json;
using Wfx.Core;

namespace Wfx.Tools.Tests;

public sealed class SkillToolTests
{
    [Fact]
    public async Task ReturnsFullSkillBody()
    {
        using var userProfile = new TemporaryDirectory();
        using var workspace = new TemporaryDirectory();

        var skillDir = Path.Combine(userProfile.Path, ".wfx", "skills", "git-guardrails");
        Directory.CreateDirectory(skillDir);
        var skillBody = """
            ---
            name: git-guardrails
            description: Prevent dangerous git operations.
            ---

            # Git Guardrails

            Refuse destructive git commands.
            """;
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), skillBody);

        var locator = SkillLocator.Discover(userProfile.Path, workspace.Path);
        var tool = new SkillTool(locator);

        var result = await tool.ExecuteAsync(
            JsonDocument.Parse("{\"name\": \"git-guardrails\"}").RootElement,
            new ToolContext(workspace.Path),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(skillBody, result.Output);
    }

    [Fact]
    public async Task ReturnsWorkspaceSkillWhenNameCollidesWithUserSkill()
    {
        using var userProfile = new TemporaryDirectory();
        using var workspace = new TemporaryDirectory();

        var userSkillDir = Path.Combine(userProfile.Path, ".wfx", "skills", "shared");
        Directory.CreateDirectory(userSkillDir);
        File.WriteAllText(
            Path.Combine(userSkillDir, "SKILL.md"),
            """
            ---
            name: shared
            description: User version.
            ---

            User body.
            """);

        var workspaceSkillDir = Path.Combine(workspace.Path, ".wfx", "skills", "shared");
        Directory.CreateDirectory(workspaceSkillDir);
        var workspaceBody = """
            ---
            name: shared
            description: Workspace version.
            ---

            Workspace body.
            """;
        File.WriteAllText(Path.Combine(workspaceSkillDir, "SKILL.md"), workspaceBody);

        var locator = SkillLocator.Discover(userProfile.Path, workspace.Path);
        var tool = new SkillTool(locator);

        var result = await tool.ExecuteAsync(
            JsonDocument.Parse("{\"name\": \"shared\"}").RootElement,
            new ToolContext(workspace.Path),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(workspaceBody, result.Output);
    }

    [Fact]
    public async Task FailsForUnknownSkill()
    {
        using var userProfile = new TemporaryDirectory();
        using var workspace = new TemporaryDirectory();

        var locator = SkillLocator.Discover(userProfile.Path, workspace.Path);
        var tool = new SkillTool(locator);

        var result = await tool.ExecuteAsync(
            JsonDocument.Parse("{\"name\": \"unknown\"}").RootElement,
            new ToolContext(workspace.Path),
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("unknown", result.Error);
    }

    [Fact]
    public void ClassifiesAsReadOnly()
    {
        using var userProfile = new TemporaryDirectory();
        using var workspace = new TemporaryDirectory();

        var locator = SkillLocator.Discover(userProfile.Path, workspace.Path);
        var tool = new SkillTool(locator);

        Assert.Equal(ApprovalLevel.ReadOnly, tool.Classify(JsonDocument.Parse("{\"name\": \"x\"}").RootElement));
    }

    [Fact]
    public void PublishesSkillSchema()
    {
        using var userProfile = new TemporaryDirectory();
        using var workspace = new TemporaryDirectory();

        var locator = SkillLocator.Discover(userProfile.Path, workspace.Path);
        var tool = new SkillTool(locator);

        Assert.Equal("skill", tool.Definition.Name);
        Assert.Contains("name", tool.Definition.Parameters["required"]!.AsArray().Select(static n => n!.GetValue<string>()));
        Assert.Equal("object", tool.Definition.Parameters["type"]!.GetValue<string>());
    }
}
