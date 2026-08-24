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

    [Theory]
    [InlineData(ApprovalMode.Always, "always")]
    [InlineData(ApprovalMode.Workspace, "workspace")]
    [InlineData(ApprovalMode.Never, "never")]
    [InlineData(ApprovalMode.AllowAll, "yolo")]
    public void FormatApprovalMode_UsesConfigurationNames(ApprovalMode mode, string expected)
    {
        Assert.Equal(expected, WfxConfiguration.FormatApprovalMode(mode));
    }

    [Fact]
    public void Load_AcceptsYoloFromEnvironment()
    {
        using var workspace = new TemporaryDirectory();

        var result = WfxConfiguration.Load(
            workspace.Path,
            environment: new Dictionary<string, string?> { ["WFX_APPROVAL"] = "yolo" },
            userProfile: Path.Combine(workspace.Path, "missing-profile"));

        Assert.Equal(ApprovalMode.AllowAll, result.Approval);
    }

    [Fact]
    public void Load_RejectsInvalidApprovalFromEnvironment()
    {
        using var workspace = new TemporaryDirectory();

        var exception = Assert.Throws<InvalidOperationException>(() => WfxConfiguration.Load(
            workspace.Path,
            environment: new Dictionary<string, string?> { ["WFX_APPROVAL"] = "sometimes" },
            userProfile: Path.Combine(workspace.Path, "missing-profile")));

        Assert.Contains("always, workspace, never, or yolo", exception.Message);
    }

    [Fact]
    public void Load_ReportsPerLayerSources()
    {
        using var workspace = new TemporaryDirectory();
        using var profile = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(profile.Path, ".wfx"));
        Directory.CreateDirectory(Path.Combine(workspace.Path, ".wfx"));
        var userPath = Path.GetFullPath(Path.Combine(profile.Path, ".wfx", "config.json"));
        var projectPath = Path.GetFullPath(Path.Combine(workspace.Path, ".wfx", "config.json"));
        File.WriteAllText(userPath, """{ "max_iterations": 10 }""");
        File.WriteAllText(projectPath, """{ "base_url": "https://project.example/v1" }""");
        var environment = new Dictionary<string, string?>
        {
            ["WFX_MODEL"] = "environment-model"
        };

        var result = WfxConfiguration.Load(
            workspace.Path,
            new WfxSettingsLayer { Approval = ApprovalMode.Never },
            environment,
            profile.Path);

        Assert.Collection(
            result.Sources,
            source =>
            {
                Assert.Equal("defaults", source.Layer);
                Assert.Null(source.Path);
                Assert.Equal(new[] { "provider", "timeout_seconds" }, source.Keys);
            },
            source =>
            {
                Assert.Equal("user", source.Layer);
                Assert.Equal(userPath, source.Path);
                Assert.Equal(new[] { "max_iterations" }, source.Keys);
            },
            source =>
            {
                Assert.Equal("project", source.Layer);
                Assert.Equal(projectPath, source.Path);
                Assert.Equal(new[] { "base_url" }, source.Keys);
            },
            source =>
            {
                Assert.Equal("environment", source.Layer);
                Assert.Null(source.Path);
                Assert.Equal(new[] { "model" }, source.Keys);
            },
            source =>
            {
                Assert.Equal("cli", source.Layer);
                Assert.Null(source.Path);
                Assert.Equal(new[] { "approval" }, source.Keys);
            });
    }

    [Fact]
    public void Load_SourcesAttributeSuppressedCredentialsToProjectLayer()
    {
        using var workspace = new TemporaryDirectory();
        using var profile = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(profile.Path, ".wfx"));
        Directory.CreateDirectory(Path.Combine(workspace.Path, ".wfx"));
        File.WriteAllText(Path.Combine(profile.Path, ".wfx", "config.json"), """
            { "api_key": "user-secret" }
            """);
        File.WriteAllText(Path.Combine(workspace.Path, ".wfx", "config.json"), """
            { "base_url": "https://gateway.example/v1", "api_key": "project-secret" }
            """);
        var environment = new Dictionary<string, string?>
        {
            ["WFX_API_KEY"] = "generic-secret"
        };

        var result = WfxConfiguration.Load(workspace.Path, environment: environment, userProfile: profile.Path);

        Assert.Equal("project-secret", result.ApiKey);
        var project = result.Sources.Single(source => source.Layer == "project");
        Assert.Equal(new[] { "base_url", "api_key" }, project.Keys);
        Assert.DoesNotContain(result.Sources, source => source.Layer is "user" or "environment");
    }

    [Fact]
    public void Load_SourcesAttributeAmbientCredentialToEnvironmentLayer()
    {
        using var workspace = new TemporaryDirectory();

        var result = WfxConfiguration.Load(
            workspace.Path,
            environment: new Dictionary<string, string?> { ["OPENAI_API_KEY"] = "ambient-secret" },
            userProfile: Path.Combine(workspace.Path, "missing-profile"));

        Assert.Equal("ambient-secret", result.ApiKey);
        var environment = result.Sources.Single(source => source.Layer == "environment");
        Assert.Equal(new[] { "api_key" }, environment.Keys);
        // base_url derives from the provider default; no layer supplied it.
        Assert.DoesNotContain(
            result.Sources,
            source => source.Keys.Contains("base_url"));
    }

    [Fact]
    public void Load_ConfiguredModelProfilesExposeEndpointAndCredentials()
    {
        using var workspace = new TemporaryDirectory();
        using var profile = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(profile.Path, ".wfx"));
        File.WriteAllText(Path.Combine(profile.Path, ".wfx", "config.json"), """
            {
              "profiles": {
                "cred": { "model": "cred-model", "api_key": "profile-secret" },
                "bare": { "model": "bare-model" },
                "nomodel": { "base_url": "https://no-model.example/v1" }
              }
            }
            """);

        var result = WfxConfiguration.Load(
            workspace.Path,
            environment: new Dictionary<string, string?>(),
            userProfile: profile.Path);

        // Only profiles carrying a model key appear.
        Assert.Collection(
            result.ConfiguredModelProfiles,
            entry =>
            {
                Assert.Equal("bare", entry.Name);
                Assert.Equal("openai", entry.Provider);
                Assert.Equal("chat_completions", entry.Protocol);
                Assert.Equal(new Uri("https://api.openai.com/v1"), entry.BaseUri);
                Assert.Equal("bare-model", entry.Model);
                Assert.False(entry.HasCredentials);
                Assert.Null(entry.Error);
            },
            entry =>
            {
                Assert.Equal("cred", entry.Name);
                Assert.Equal("cred-model", entry.Model);
                Assert.True(entry.HasCredentials);
                Assert.Null(entry.Error);
            });
    }

    [Fact]
    public void Load_ConfiguredModelProfileCredentialsReflectAmbientCredential()
    {
        using var workspace = new TemporaryDirectory();
        using var profile = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(profile.Path, ".wfx"));
        File.WriteAllText(Path.Combine(profile.Path, ".wfx", "config.json"), """
            { "profiles": { "bare": { "model": "bare-model" } } }
            """);

        var withoutCredential = WfxConfiguration.Load(
            workspace.Path,
            environment: new Dictionary<string, string?>(),
            userProfile: profile.Path);
        Assert.False(Assert.Single(withoutCredential.ConfiguredModelProfiles).HasCredentials);

        var withCredential = WfxConfiguration.Load(
            workspace.Path,
            environment: new Dictionary<string, string?> { ["OPENAI_API_KEY"] = "ambient-secret" },
            userProfile: profile.Path);
        Assert.True(Assert.Single(withCredential.ConfiguredModelProfiles).HasCredentials);
    }

    [Fact]
    public void Load_ConfiguredModelProfilesCarryResolutionError()
    {
        using var workspace = new TemporaryDirectory();
        using var profile = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(profile.Path, ".wfx"));
        File.WriteAllText(Path.Combine(profile.Path, ".wfx", "config.json"), """
            {
              "profiles": {
                "broken": { "model": "broken-model", "protocol": "anthropic_messages" }
              }
            }
            """);

        var result = WfxConfiguration.Load(
            workspace.Path,
            environment: new Dictionary<string, string?>(),
            userProfile: profile.Path);

        var entry = Assert.Single(result.ConfiguredModelProfiles);
        Assert.Equal("broken", entry.Name);
        Assert.Equal("broken-model", entry.Model);
        Assert.Null(entry.Protocol);
        Assert.Null(entry.BaseUri);
        Assert.False(entry.HasCredentials);
        Assert.NotNull(entry.Error);
        Assert.Contains("anthropic_messages", entry.Error);
    }

    [Fact]
    public void Load_SourcesAttributeProfileSelectionToCliLayer()
    {
        using var workspace = new TemporaryDirectory();
        using var profile = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(profile.Path, ".wfx"));
        File.WriteAllText(Path.Combine(profile.Path, ".wfx", "config.json"), """
            { "profiles": { "deep": { "model": "profile-model", "headers": { "X-Auth": "h" } } } }
            """);

        var result = WfxConfiguration.Load(
            workspace.Path,
            new WfxSettingsLayer { Profile = "deep" },
            new Dictionary<string, string?>(),
            profile.Path);

        Assert.Equal("profile-model", result.Model);
        var user = result.Sources.Single(source => source.Layer == "user");
        Assert.Equal(new[] { "model", "headers" }, user.Keys);
        var cli = result.Sources.Single(source => source.Layer == "cli");
        Assert.Equal(new[] { "profile" }, cli.Keys);
    }
}
