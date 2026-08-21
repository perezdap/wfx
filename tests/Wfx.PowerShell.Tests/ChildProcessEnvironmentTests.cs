using Wfx.PowerShell;

namespace Wfx.PowerShell.Tests;

public sealed class ChildProcessEnvironmentTests
{
    [Theory]
    [InlineData("WFX_API_KEY")]
    [InlineData("OPENAI_API_KEY")]
    [InlineData("OPENROUTER_API_KEY")]
    [InlineData("VENICE_API_KEY")]
    [InlineData("GEMINI_API_KEY")]
    [InlineData("GITHUB_TOKEN")]
    [InlineData("wfx_api_key")]
    [InlineData("APP_SECRET")]
    public void TreatsDocumentedSecretNamesAsSecret(string name) =>
        Assert.True(ChildProcessEnvironment.IsSecretVariableName(name));

    [Theory]
    [InlineData("PATH")]
    [InlineData("WFX_MODEL")]
    [InlineData("WFX_PROVIDER")]
    [InlineData("USERNAME")]
    [InlineData("GIT_PAGER")]
    public void DoesNotTreatOrdinaryNamesAsSecret(string name) =>
        Assert.False(ChildProcessEnvironment.IsSecretVariableName(name));

    [Fact]
    public void ApplyRemovesSecretNamesAndKeepsOthers()
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["WFX_API_KEY"] = "secret",
            ["VENICE_API_KEY"] = "secret",
            ["GITHUB_TOKEN"] = "secret",
            ["APP_SECRET"] = "secret",
            ["PATH"] = @"C:\Windows",
            ["WFX_MODEL"] = "gpt"
        };

        ChildProcessEnvironment.Apply(environment);

        Assert.False(environment.ContainsKey("WFX_API_KEY"));
        Assert.False(environment.ContainsKey("VENICE_API_KEY"));
        Assert.False(environment.ContainsKey("GITHUB_TOKEN"));
        Assert.False(environment.ContainsKey("APP_SECRET"));
        Assert.Equal(@"C:\Windows", environment["PATH"]);
        Assert.Equal("gpt", environment["WFX_MODEL"]);
    }

    [Fact]
    public void ApplyRestoresOnlyOverlayVariables()
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["WFX_API_KEY"] = "inherited",
            ["VENICE_API_KEY"] = "inherited"
        };

        ChildProcessEnvironment.Apply(environment, new Dictionary<string, string?>
        {
            ["WFX_API_KEY"] = "opt-in"
        });

        Assert.Equal("opt-in", environment["WFX_API_KEY"]);
        Assert.False(environment.ContainsKey("VENICE_API_KEY"));
    }

    [Fact]
    public void ApplySetsPagerDefaultsToCat()
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["GIT_PAGER"] = "less",
            ["PAGER"] = "more"
        };

        ChildProcessEnvironment.Apply(environment);

        Assert.Equal("cat", environment["GIT_PAGER"]);
        Assert.Equal("cat", environment["PAGER"]);
    }

    [Fact]
    public void ApplyLetsOverlayWinOverPagerDefaults()
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        ChildProcessEnvironment.Apply(environment, new Dictionary<string, string?>
        {
            ["GIT_PAGER"] = "custom-git-pager",
            ["PAGER"] = "custom-pager"
        });

        Assert.Equal("custom-git-pager", environment["GIT_PAGER"]);
        Assert.Equal("custom-pager", environment["PAGER"]);
    }
}
