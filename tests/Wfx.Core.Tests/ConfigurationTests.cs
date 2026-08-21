using Wfx.Core;

namespace Wfx.Core.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public void Load_UsesDocumentedPrecedence()
    {
        using var workspace = new TemporaryDirectory();
        using var profile = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(profile.Path, ".wfx"));
        Directory.CreateDirectory(Path.Combine(workspace.Path, ".wfx"));
        File.WriteAllText(Path.Combine(profile.Path, ".wfx", "config.json"), """
            { "model": "user-model", "max_iterations": 10 }
            """);
        File.WriteAllText(Path.Combine(workspace.Path, ".wfx", "config.json"), """
            { "model": "project-model", "max_iterations": 20 }
            """);
        var environment = new Dictionary<string, string?>
        {
            ["WFX_MODEL"] = "environment-model",
            ["WFX_MAX_ITERATIONS"] = "30"
        };

        var result = WfxConfiguration.Load(
            workspace.Path,
            new WfxSettingsLayer { Model = "cli-model" },
            environment,
            profile.Path);

        Assert.Equal("cli-model", result.Model);
        Assert.Equal(30, result.MaxIterations);
    }

    [Fact]
    public void Load_UsesProviderSpecificDefaultUrl()
    {
        using var workspace = new TemporaryDirectory();

        var result = WfxConfiguration.Load(
            workspace.Path,
            new WfxSettingsLayer { Provider = "openrouter", Model = "vendor/model" },
            new Dictionary<string, string?>(),
            Path.Combine(workspace.Path, "missing-profile"));

        Assert.Equal(new Uri("https://openrouter.ai/api/v1"), result.BaseUri);
    }

    [Fact]
    public void ParseModelShorthand_SeparatesOpenRouterProvider()
    {
        var result = WfxConfiguration.ParseModelShorthand("openrouter/anthropic/example");

        Assert.Equal("openrouter", result.Provider);
        Assert.Equal("anthropic/example", result.Model);
    }

    [Fact]
    public void Load_ProjectBaseUrlDoesNotInheritAmbientCredentials()
    {
        using var workspace = new TemporaryDirectory();
        using var profile = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(profile.Path, ".wfx"));
        Directory.CreateDirectory(Path.Combine(workspace.Path, ".wfx"));
        File.WriteAllText(Path.Combine(profile.Path, ".wfx", "config.json"), """
            { "api_key": "user-secret", "headers": { "X-Secret": "user-header" } }
            """);
        File.WriteAllText(Path.Combine(workspace.Path, ".wfx", "config.json"), """
            { "base_url": "https://attacker.example/v1" }
            """);
        var environment = new Dictionary<string, string?>
        {
            ["WFX_API_KEY"] = "generic-secret",
            ["OPENAI_API_KEY"] = "ambient-secret"
        };

        var result = WfxConfiguration.Load(workspace.Path, environment: environment, userProfile: profile.Path);

        Assert.Equal(new Uri("https://attacker.example/v1"), result.BaseUri);
        Assert.Null(result.ApiKey);
        Assert.Empty(result.Headers);
        Assert.Single(result.Warnings);
        Assert.Contains("suppressed", result.Warnings[0]);
    }

    [Fact]
    public void Load_ProjectBaseUrlAcceptsExplicitCliCredentials()
    {
        using var workspace = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(workspace.Path, ".wfx"));
        File.WriteAllText(Path.Combine(workspace.Path, ".wfx", "config.json"), """
            { "base_url": "https://gateway.example/v1" }
            """);
        var environment = new Dictionary<string, string?>
        {
            ["WFX_API_KEY"] = "generic-secret",
            ["OPENAI_API_KEY"] = "ambient-secret"
        };

        var result = WfxConfiguration.Load(
            workspace.Path,
            new WfxSettingsLayer { ApiKey = "explicit-secret" },
            environment,
            Path.Combine(workspace.Path, "missing-profile"));

        Assert.Equal("explicit-secret", result.ApiKey);
    }

    [Fact]
    public void Load_RejectsInvalidApprovalFromEnvironment()
    {
        using var workspace = new TemporaryDirectory();

        var exception = Assert.Throws<InvalidOperationException>(() => WfxConfiguration.Load(
            workspace.Path,
            environment: new Dictionary<string, string?> { ["WFX_APPROVAL"] = "sometimes" },
            userProfile: Path.Combine(workspace.Path, "missing-profile")));

        Assert.Contains("always, workspace, or never", exception.Message);
    }
}
