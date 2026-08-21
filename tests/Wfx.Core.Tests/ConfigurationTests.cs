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
    public void Load_WarnsWhenProjectBaseUrlSuppressesUserApiKeyAlone()
    {
        using var workspace = new TemporaryDirectory();
        using var profile = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(profile.Path, ".wfx"));
        Directory.CreateDirectory(Path.Combine(workspace.Path, ".wfx"));
        File.WriteAllText(Path.Combine(profile.Path, ".wfx", "config.json"), """
            { "api_key": "user-secret" }
            """);
        File.WriteAllText(Path.Combine(workspace.Path, ".wfx", "config.json"), """
            { "base_url": "https://gateway.example/v1" }
            """);

        var result = WfxConfiguration.Load(
            workspace.Path,
            environment: new Dictionary<string, string?>(),
            userProfile: profile.Path);

        Assert.Null(result.ApiKey);
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
    public void Load_SameFileUserAndProjectConfigDoesNotWarnAboutSuppression()
    {
        // Running wfx from the profile root makes the user config and the project
        // config the same file; nothing is suppressed and no warning should fire.
        using var root = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(root.Path, ".wfx"));
        File.WriteAllText(Path.Combine(root.Path, ".wfx", "config.json"), """
            { "base_url": "https://gateway.example/v1", "api_key": "file-secret", "model": "m" }
            """);

        var result = WfxConfiguration.Load(
            root.Path,
            environment: new Dictionary<string, string?>(),
            userProfile: root.Path);

        Assert.Equal("file-secret", result.ApiKey);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Load_SameFileUserAndProjectConfigThroughProfileExpansion()
    {
        using var root = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(root.Path, ".wfx"));
        File.WriteAllText(Path.Combine(root.Path, ".wfx", "config.json"), """
            { "profile": "cloud", "profiles": { "cloud": { "base_url": "https://gateway.example/v1", "api_key": "profile-secret", "model": "m" } } }
            """);

        var result = WfxConfiguration.Load(
            root.Path,
            environment: new Dictionary<string, string?>(),
            userProfile: root.Path);

        Assert.Equal("profile-secret", result.ApiKey);
        Assert.Equal("m", result.Model);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Load_SameFileDetectedAcrossWindowsPathCasing()
    {
        using var root = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(root.Path, ".wfx"));
        File.WriteAllText(Path.Combine(root.Path, ".wfx", "config.json"), """
            { "base_url": "https://gateway.example/v1", "api_key": "file-secret", "model": "m" }
            """);

        // Flip the case of every letter deterministically so the test truly
        // exercises case-insensitive comparison even on lowercase temp roots.
        var result = WfxConfiguration.Load(
            root.Path,
            environment: new Dictionary<string, string?>(),
            userProfile: FlipCasing(root.Path));

        Assert.Equal("file-secret", result.ApiKey);
        Assert.Empty(result.Warnings);
    }

    private static string FlipCasing(string path)
    {
        var chars = path.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (char.IsLetter(chars[i]))
            {
                chars[i] = char.IsUpper(chars[i])
                    ? char.ToLowerInvariant(chars[i])
                    : char.ToUpperInvariant(chars[i]);
            }
        }

        return new string(chars);
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
