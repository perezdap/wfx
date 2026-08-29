using Wfx.Core;
using Wfx.Tools;

namespace Wfx.Tools.Tests;

public sealed class BuiltInToolsTests
{
    [Fact]
    public void RegistersExpectedMilestoneOneTools()
    {
        using var workspace = new TemporaryDirectory();
        var registry = BuiltInTools.Create(workspace.Path);

        Assert.Equal(
            ["apply_patch", "git", "list_directory", "powershell", "read_file", "search_files", "search_text", "write_file"],
            registry.Definitions.Select(static definition => definition.Name).Order().ToArray());
        Assert.All(registry.Definitions, static definition =>
            Assert.Equal("object", definition.Parameters["type"]!.GetValue<string>()));
    }

    [Fact]
    public void RegistersSkillToolWhenSkillsAreAvailable()
    {
        using var workspace = new TemporaryDirectory();
        using var userProfile = new TemporaryDirectory();

        var skillDir = Path.Combine(userProfile.Path, ".wfx", "skills", "git-guardrails");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(
            Path.Combine(skillDir, "SKILL.md"),
            """
            ---
            name: git-guardrails
            description: Prevent dangerous git operations.
            ---

            # Git Guardrails
            """);

        var skills = SkillLocator.Discover(userProfile.Path, workspace.Path, TestContext.Current.CancellationToken);
        var registry = BuiltInTools.Create(workspace.Path, skills);

        Assert.Contains("skill", registry.Definitions.Select(static definition => definition.Name));
    }
}
