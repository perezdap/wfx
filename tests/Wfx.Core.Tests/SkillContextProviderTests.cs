using System.Text;
using Wfx.Core;

namespace Wfx.Core.Tests;

public sealed class SkillContextProviderTests
{
    [Fact]
    public async Task ReturnsNullWhenNoSkillsAvailable()
    {
        var locator = SkillLocator.Discover(null, null, TestContext.Current.CancellationToken);
        var provider = new SkillContextProvider(locator);

        var context = await provider.GetContextAsync(TestContext.Current.CancellationToken);

        Assert.Null(context);
    }

    [Fact]
    public async Task ListsSkillNamesAndDescriptionsOnly()
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

        var locator = SkillLocator.Discover(userProfile.Path, workspace.Path, TestContext.Current.CancellationToken);
        var provider = new SkillContextProvider(locator);

        var context = await provider.GetContextAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(context);
        Assert.Contains("git-guardrails", context);
        Assert.Contains("Prevent dangerous git operations.", context);
        Assert.DoesNotContain("Refuse destructive git commands.", context);
        Assert.Contains("Available skills:", context);
    }

    [Fact]
    public async Task ListsSkillsInAlphabeticalOrder()
    {
        using var userProfile = new TemporaryDirectory();
        using var workspace = new TemporaryDirectory();

        var firstDir = Path.Combine(userProfile.Path, ".wfx", "skills", "zebra");
        Directory.CreateDirectory(firstDir);
        File.WriteAllText(
            Path.Combine(firstDir, "SKILL.md"),
            """
            ---
            name: zebra
            description: Last alphabetically.
            ---

            Body.
            """);

        var secondDir = Path.Combine(userProfile.Path, ".wfx", "skills", "alpha");
        Directory.CreateDirectory(secondDir);
        File.WriteAllText(
            Path.Combine(secondDir, "SKILL.md"),
            """
            ---
            name: alpha
            description: First alphabetically.
            ---

            Body.
            """);

        var locator = SkillLocator.Discover(userProfile.Path, workspace.Path, TestContext.Current.CancellationToken);
        var provider = new SkillContextProvider(locator);

        var context = await provider.GetContextAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(context);
        var alphaIndex = context.IndexOf("alpha", StringComparison.Ordinal);
        var zebraIndex = context.IndexOf("zebra", StringComparison.Ordinal);
        Assert.True(alphaIndex < zebraIndex);
    }
}
