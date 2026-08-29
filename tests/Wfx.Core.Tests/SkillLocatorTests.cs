using Wfx.Core;

namespace Wfx.Core.Tests;

public sealed class SkillLocatorTests
{
    [Fact]
    public void DiscoversUserSkill()
    {
        using var userProfile = new TemporaryDirectory();
        using var workspace = new TemporaryDirectory();
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

            Refuse destructive git commands.
            """);

        var locator = SkillLocator.Discover(userProfile.Path, workspace.Path);

        Assert.Single(locator.Skills);
        Assert.Contains("git-guardrails", locator.Skills);
        Assert.Equal("Prevent dangerous git operations.", locator.Skills["git-guardrails"].Description);
        Assert.Equal(SkillSource.User, locator.Skills["git-guardrails"].Source);
        Assert.Contains("Refuse destructive git commands.", locator.Skills["git-guardrails"].Body);
    }

    [Fact]
    public void DiscoversWorkspaceSkill()
    {
        using var userProfile = new TemporaryDirectory();
        using var workspace = new TemporaryDirectory();
        var skillDir = Path.Combine(workspace.Path, ".wfx", "skills", "issue-triage");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(
            Path.Combine(skillDir, "SKILL.md"),
            """
            ---
            name: issue-triage
            description: Triage GitHub issues.
            ---

            # Issue Triage

            Label and route issues.
            """);

        var locator = SkillLocator.Discover(userProfile.Path, workspace.Path);

        Assert.Single(locator.Skills);
        Assert.Equal("Triage GitHub issues.", locator.Skills["issue-triage"].Description);
        Assert.Equal(SkillSource.Workspace, locator.Skills["issue-triage"].Source);
    }

    [Fact]
    public void WorkspaceSkillWinsOverUserSkillWithSameName()
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
        File.WriteAllText(
            Path.Combine(workspaceSkillDir, "SKILL.md"),
            """
            ---
            name: shared
            description: Workspace version.
            ---

            Workspace body.
            """);

        var locator = SkillLocator.Discover(userProfile.Path, workspace.Path);

        Assert.Single(locator.Skills);
        Assert.Equal("Workspace version.", locator.Skills["shared"].Description);
        Assert.Equal(SkillSource.Workspace, locator.Skills["shared"].Source);
        Assert.Contains("Workspace body.", locator.Skills["shared"].Body);
    }

    [Fact]
    public void SkipsSkillWhenNameDoesNotMatchDirectory()
    {
        using var userProfile = new TemporaryDirectory();
        using var workspace = new TemporaryDirectory();

        var skillDir = Path.Combine(userProfile.Path, ".wfx", "skills", "foo");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(
            Path.Combine(skillDir, "SKILL.md"),
            """
            ---
            name: bar
            description: Mismatched name.
            ---

            Body.
            """);

        var locator = SkillLocator.Discover(userProfile.Path, workspace.Path);

        Assert.Empty(locator.Skills);
        Assert.Contains("foo", Assert.Single(locator.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void SkipsSkillWithMissingDescriptionAndWarns()
    {
        using var userProfile = new TemporaryDirectory();
        using var workspace = new TemporaryDirectory();

        var skillDir = Path.Combine(userProfile.Path, ".wfx", "skills", "no-desc");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(
            Path.Combine(skillDir, "SKILL.md"),
            """
            ---
            name: no-desc
            ---

            Body.
            """);

        var locator = SkillLocator.Discover(userProfile.Path, workspace.Path);

        Assert.Empty(locator.Skills);
        Assert.Single(locator.Warnings);
    }

    [Fact]
    public void ReturnsEmptyWhenNoSkillDirectoriesExist()
    {
        using var userProfile = new TemporaryDirectory();
        using var workspace = new TemporaryDirectory();

        var locator = SkillLocator.Discover(userProfile.Path, workspace.Path);

        Assert.Empty(locator.Skills);
        Assert.Empty(locator.Warnings);
    }

    [Fact]
    public void SkipsMalformedFrontmatterWithWarning()
    {
        using var userProfile = new TemporaryDirectory();
        using var workspace = new TemporaryDirectory();

        var skillDir = Path.Combine(userProfile.Path, ".wfx", "skills", "bad");
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(
            Path.Combine(skillDir, "SKILL.md"),
            """
            ---
            not a valid frontmatter line without colon value
            ---

            Body.
            """);

        var locator = SkillLocator.Discover(userProfile.Path, workspace.Path);

        Assert.Empty(locator.Skills);
        Assert.Single(locator.Warnings);
    }
}
