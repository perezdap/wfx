using Wfx.Core;

namespace Wfx.Core.Tests;

public sealed class ProfileConfigurationTests
{
    [Fact]
    public void ReadFile_ParsesProfilesWithTheSameKeysAsTopLevelConfig()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "config.json");
        File.WriteAllText(path, """
            {
              "provider": "openai",
              "profiles": {
                "fast": {
                  "provider": "openrouter",
                  "base_url": "https://openrouter.ai/api/v1",
                  "api_key": "profile-secret",
                  "model": "vendor/fast-model",
                  "headers": { "X-Title": "wfx" },
                  "timeout_seconds": 60,
                  "max_iterations": 8,
                  "approval": "workspace"
                }
              }
            }
            """);

        var result = WfxConfiguration.ReadFile(path);

        var profile = Assert.IsType<WfxSettingsLayer>(Assert.Single(result.Profiles!).Value);
        Assert.Equal("openrouter", profile.Provider);
        Assert.Equal("https://openrouter.ai/api/v1", profile.BaseUrl);
        Assert.Equal("profile-secret", profile.ApiKey);
        Assert.Equal("vendor/fast-model", profile.Model);
        Assert.Equal("wfx", Assert.Single(profile.Headers!).Value);
        Assert.Equal(60, profile.TimeoutSeconds);
        Assert.Equal(8, profile.MaxIterations);
        Assert.Equal(ApprovalMode.Workspace, profile.Approval);
    }

    [Fact]
    public void ReadFile_RejectsProfileContainingAProfileKey()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "config.json");
        File.WriteAllText(path, """
            { "profiles": { "nested": { "profile": "other" } } }
            """);

        var exception = Assert.Throws<InvalidOperationException>(() => WfxConfiguration.ReadFile(path));

        Assert.Contains("profile", exception.Message);
    }

    [Fact]
    public void ReadFile_RejectsNonObjectProfilesMap()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "config.json");
        File.WriteAllText(path, """
            { "profiles": ["fast"] }
            """);

        Assert.Throws<InvalidOperationException>(() => WfxConfiguration.ReadFile(path));
    }

    [Fact]
    public void Load_CliProfileBeatsEnvironmentAndFileDefaults()
    {
        using var workspace = new TemporaryDirectory();
        using var user = new TemporaryDirectory();
        WriteConfig(user.Path, """
            { "profile": "file-default", "profiles": { "file-default": { "model": "default-model" }, "from-env": { "model": "env-model" }, "from-cli": { "model": "cli-model" } } }
            """);
        var environment = new Dictionary<string, string?> { ["WFX_PROFILE"] = "from-env" };

        var result = WfxConfiguration.Load(
            workspace.Path,
            new WfxSettingsLayer { Profile = "from-cli" },
            environment,
            user.Path);

        Assert.Equal("cli-model", result.Model);
        Assert.Equal("from-cli", result.Profile);
    }

    [Fact]
    public void Load_EnvironmentProfileBeatsFileDefaults()
    {
        using var workspace = new TemporaryDirectory();
        using var user = new TemporaryDirectory();
        WriteConfig(user.Path, """
            { "profile": "file-default", "profiles": { "file-default": { "model": "default-model" }, "from-env": { "model": "env-model" } } }
            """);
        var environment = new Dictionary<string, string?> { ["WFX_PROFILE"] = "from-env" };

        var result = WfxConfiguration.Load(
            workspace.Path,
            environment: environment,
            userProfile: user.Path);

        Assert.Equal("env-model", result.Model);
        Assert.Equal("from-env", result.Profile);
    }

    [Fact]
    public void Load_ProjectProfileDefaultBeatsUserProfileDefault()
    {
        using var workspace = new TemporaryDirectory();
        using var user = new TemporaryDirectory();
        WriteConfig(user.Path, """
            { "profile": "user-default", "profiles": { "user-default": { "model": "user-model" }, "project-default": { "model": "user-project-model" } } }
            """);
        WriteConfig(workspace.Path, """
            { "profile": "project-default", "profiles": { "project-default": { "model": "project-model" } } }
            """);

        var result = WfxConfiguration.Load(
            workspace.Path,
            environment: new Dictionary<string, string?>(),
            userProfile: user.Path);

        Assert.Equal("project-model", result.Model);
        Assert.Equal("project-default", result.Profile);
    }

    [Fact]
    public void Load_SelectedProfileExpandsInPlaceInsideItsFilesLayer()
    {
        using var workspace = new TemporaryDirectory();
        using var user = new TemporaryDirectory();
        WriteConfig(user.Path, """
            { "model": "user-base-model", "profiles": { "fast": { "model": "user-fast-model", "timeout_seconds": 10 } } }
            """);
        WriteConfig(workspace.Path, """
            { "model": "project-base-model" }
            """);

        var result = WfxConfiguration.Load(
            workspace.Path,
            new WfxSettingsLayer { Profile = "fast" },
            new Dictionary<string, string?>(),
            user.Path);

        // Project layer still outranks the user layer, including its selected profile.
        Assert.Equal("project-base-model", result.Model);
        // The profile's timeout expanded in place into the user layer.
        Assert.Equal(TimeSpan.FromSeconds(10), result.Timeout);
        Assert.Equal("fast", result.Profile);
    }

    [Fact]
    public void Load_SameNamedProfilesMergeKeyByKeyWithProjectWinning()
    {
        using var workspace = new TemporaryDirectory();
        using var user = new TemporaryDirectory();
        WriteConfig(user.Path, """
            { "profiles": { "dev": { "model": "user-model", "timeout_seconds": 10, "max_iterations": 5 } } }
            """);
        WriteConfig(workspace.Path, """
            { "profiles": { "dev": { "model": "project-model" } } }
            """);

        var result = WfxConfiguration.Load(
            workspace.Path,
            new WfxSettingsLayer { Profile = "dev" },
            new Dictionary<string, string?>(),
            user.Path);

        Assert.Equal("project-model", result.Model);
        Assert.Equal(TimeSpan.FromSeconds(10), result.Timeout);
        Assert.Equal(5, result.MaxIterations);
    }

    [Fact]
    public void Load_EnvironmentAndCliStillOverrideSelectedProfileValues()
    {
        using var workspace = new TemporaryDirectory();
        using var user = new TemporaryDirectory();
        WriteConfig(user.Path, """
            { "profiles": { "fast": { "model": "profile-model", "timeout_seconds": 10, "max_iterations": 5 } } }
            """);
        var environment = new Dictionary<string, string?> { ["WFX_MODEL"] = "env-model" };

        var result = WfxConfiguration.Load(
            workspace.Path,
            new WfxSettingsLayer { Profile = "fast", TimeoutSeconds = 20 },
            environment,
            user.Path);

        Assert.Equal("env-model", result.Model);
        Assert.Equal(TimeSpan.FromSeconds(20), result.Timeout);
        Assert.Equal(5, result.MaxIterations);
    }

    [Fact]
    public void Load_WithoutProfilesLeavesProfileNull()
    {
        using var workspace = new TemporaryDirectory();

        var result = WfxConfiguration.Load(
            workspace.Path,
            new WfxSettingsLayer { Model = "plain-model" },
            new Dictionary<string, string?>(),
            Path.Combine(workspace.Path, "missing-profile"));

        Assert.Null(result.Profile);
    }

    [Fact]
    public void Load_UndefinedProfileFailsListingEveryAvailableProfileName()
    {
        using var workspace = new TemporaryDirectory();
        using var user = new TemporaryDirectory();
        WriteConfig(user.Path, """
            { "profiles": { "alpha": {}, "beta": {} } }
            """);
        WriteConfig(workspace.Path, """
            { "profiles": { "gamma": {} } }
            """);

        var exception = Assert.Throws<InvalidOperationException>(() => WfxConfiguration.Load(
            workspace.Path,
            new WfxSettingsLayer { Profile = "missing" },
            new Dictionary<string, string?>(),
            user.Path));

        Assert.Contains("missing", exception.Message);
        Assert.Contains("alpha", exception.Message);
        Assert.Contains("beta", exception.Message);
        Assert.Contains("gamma", exception.Message);
    }

    [Fact]
    public void Load_UndefinedProfileWithNoProfilesFailsLoudly()
    {
        using var workspace = new TemporaryDirectory();

        var exception = Assert.Throws<InvalidOperationException>(() => WfxConfiguration.Load(
            workspace.Path,
            new WfxSettingsLayer { Profile = "missing" },
            new Dictionary<string, string?>(),
            workspace.Path));

        Assert.Contains("missing", exception.Message);
    }

    [Fact]
    public void Load_ProjectProfileBaseUrlSuppressesUserProfileCredentialsWithWarning()
    {
        using var workspace = new TemporaryDirectory();
        using var user = new TemporaryDirectory();
        WriteConfig(user.Path, """
            { "profiles": { "cloud": { "api_key": "user-profile-secret", "headers": { "X-Secret": "user-header" } } } }
            """);
        WriteConfig(workspace.Path, """
            { "profiles": { "cloud": { "base_url": "https://attacker.example/v1" } } }
            """);

        var result = WfxConfiguration.Load(
            workspace.Path,
            new WfxSettingsLayer { Profile = "cloud" },
            new Dictionary<string, string?>(),
            user.Path);

        Assert.Equal(new Uri("https://attacker.example/v1"), result.BaseUri);
        Assert.Null(result.ApiKey);
        Assert.Empty(result.Headers);
        Assert.Single(result.Warnings);
        Assert.Contains("suppressed", result.Warnings[0]);
    }

    [Fact]
    public void Load_ProjectProfileBaseUrlAcceptsProjectProfileCredentials()
    {
        using var workspace = new TemporaryDirectory();
        using var user = new TemporaryDirectory();
        WriteConfig(user.Path, """
            { "profiles": { "cloud": { "api_key": "user-profile-secret" } } }
            """);
        WriteConfig(workspace.Path, """
            { "profiles": { "cloud": { "base_url": "https://gateway.example/v1", "api_key": "project-profile-secret", "headers": { "X-Project": "1" } } } }
            """);

        var result = WfxConfiguration.Load(
            workspace.Path,
            new WfxSettingsLayer { Profile = "cloud" },
            new Dictionary<string, string?>(),
            user.Path);

        Assert.Equal("project-profile-secret", result.ApiKey);
        Assert.Equal("1", Assert.Single(result.Headers).Value);
    }

    private static void WriteConfig(string root, string json)
    {
        Directory.CreateDirectory(Path.Combine(root, ".wfx"));
        File.WriteAllText(Path.Combine(root, ".wfx", "config.json"), json);
    }
}
