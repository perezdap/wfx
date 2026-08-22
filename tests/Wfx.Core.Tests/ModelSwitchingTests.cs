using Wfx.Core;

namespace Wfx.Core.Tests;

public sealed class ModelSwitchingTests
{
    [Fact]
    public void PickerSelectionAdoptsTheConfiguredModelsConnection()
    {
        using var workspace = new TemporaryDirectory();
        using var user = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(user.Path, ".wfx"));
        File.WriteAllText(Path.Combine(user.Path, ".wfx", "config.json"), """
            {
              "provider": "local",
              "base_url": "http://localhost:1234/v1",
              "model": "local-model",
              "profiles": {
                "cloud": {
                  "provider": "openrouter",
                  "protocol": "responses",
                  "base_url": "https://openrouter.example/v1",
                  "api_key": "cloud-secret",
                  "model": "cloud-model",
                  "headers": { "X-Cloud": "1" },
                  "timeout_seconds": 45
                }
              }
            }
            """);
        var current = WfxConfiguration.Load(
            workspace.Path,
            environment: new Dictionary<string, string?>(),
            userProfile: user.Path);

        var result = ModelSwitchResolver.Resolve(current, ModelSwitchRequest.Picker("1"));

        Assert.True(result.Succeeded);
        Assert.True(result.TransportChanged);
        Assert.NotNull(result.Settings);
        Assert.Equal("cloud", result.Settings.Profile);
        Assert.Equal("openrouter", result.Settings.Provider);
        Assert.Equal("responses", result.Settings.Protocol);
        Assert.Equal(new Uri("https://openrouter.example/v1"), result.Settings.BaseUri);
        Assert.Equal("cloud-secret", result.Settings.ApiKey);
        Assert.Equal("cloud-model", result.Settings.Model);
        Assert.Equal("1", Assert.Single(result.Settings.Headers).Value);
        Assert.Equal(TimeSpan.FromSeconds(45), result.Settings.Timeout);
    }

    [Fact]
    public void PickerSelectionUsesAModelOnlySwapForTheSameConnection()
    {
        using var workspace = new TemporaryDirectory();
        using var user = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(user.Path, ".wfx"));
        File.WriteAllText(Path.Combine(user.Path, ".wfx", "config.json"), """
            {
              "provider": "openai",
              "base_url": "https://api.example/v1",
              "profiles": {
                "first": { "model": "first-model" },
                "second": { "model": "second-model" }
              }
            }
            """);
        var current = WfxConfiguration.Load(
            workspace.Path,
            new WfxSettingsLayer { Profile = "first" },
            new Dictionary<string, string?>(),
            user.Path);

        var result = ModelSwitchResolver.Resolve(current, ModelSwitchRequest.Picker("2"));

        Assert.True(result.Succeeded);
        Assert.False(result.TransportChanged);
        Assert.Equal("second", result.Settings!.Profile);
        Assert.Equal("second-model", result.Settings.Model);
        Assert.Equal(current.BaseUri, result.Settings.BaseUri);
        var conversation = new ModelMessage[]
        {
            new(ModelRole.Assistant, "answer", ProviderItemsJson: """[{"type":"reasoning"}]""")
        };
        Assert.Same(conversation, result.MapConversation(conversation));
    }

    [Fact]
    public void FreeFormModelKeepsTheCurrentConnection()
    {
        var current = new WfxSettings(
            "openrouter",
            "responses",
            new Uri("https://gateway.example/v1"),
            "secret",
            "old-model",
            new Dictionary<string, string> { ["X-Test"] = "1" },
            TimeSpan.FromSeconds(30),
            24,
            ApprovalMode.Workspace)
        {
            Profile = "cloud"
        };

        var result = ModelSwitchResolver.Resolve(current, ModelSwitchRequest.FreeForm("new/model"));

        Assert.True(result.Succeeded);
        Assert.False(result.TransportChanged);
        Assert.Equal("new/model", result.Settings!.Model);
        Assert.Equal(current.Provider, result.Settings.Provider);
        Assert.Equal(current.Protocol, result.Settings.Protocol);
        Assert.Equal(current.BaseUri, result.Settings.BaseUri);
        Assert.Equal(current.Profile, result.Settings.Profile);
    }

    [Fact]
    public void TransportChangeMapsHistoryToPortableMessages()
    {
        using var workspace = new TemporaryDirectory();
        using var user = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(user.Path, ".wfx"));
        File.WriteAllText(Path.Combine(user.Path, ".wfx", "config.json"), """
            {
              "profiles": {
                "first": {
                  "protocol": "responses",
                  "base_url": "https://first.example/v1",
                  "model": "first-model"
                },
                "second": {
                  "protocol": "responses",
                  "base_url": "https://second.example/v1",
                  "model": "second-model"
                }
              }
            }
            """);
        var current = WfxConfiguration.Load(
            workspace.Path,
            new WfxSettingsLayer { Profile = "first" },
            new Dictionary<string, string?>(),
            user.Path);
        var resolution = ModelSwitchResolver.Resolve(current, ModelSwitchRequest.Picker("2"));
        var conversation = new ModelMessage[]
        {
            new(ModelRole.System, "instructions"),
            new(ModelRole.Assistant, "portable answer", ProviderItemsJson: """[{"type":"reasoning"}]"""),
            new(ModelRole.Assistant, null, ProviderItemsJson: """[{"type":"reasoning"}]""")
        };

        var mapped = resolution.MapConversation(conversation);

        Assert.Collection(
            mapped,
            message => Assert.Equal(ModelRole.System, message.Role),
            message =>
            {
                Assert.Equal("portable answer", message.Content);
                Assert.Null(message.ProviderItemsJson);
            });
    }

    [Fact]
    public void UnknownPickerSelectionReturnsAnErrorResult()
    {
        var current = new WfxSettings(
            "openai",
            "chat_completions",
            new Uri("https://api.example/v1"),
            null,
            "model",
            new Dictionary<string, string>(),
            TimeSpan.FromSeconds(30),
            24,
            ApprovalMode.Always);

        var result = ModelSwitchResolver.Resolve(current, ModelSwitchRequest.Picker("9"));

        Assert.False(result.Succeeded);
        Assert.Null(result.Settings);
        Assert.Contains("Unknown model selection", result.Error);
    }

    [Fact]
    public void UnresolvablePickerSelectionReturnsAnErrorResult()
    {
        using var workspace = new TemporaryDirectory();
        using var user = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(user.Path, ".wfx"));
        File.WriteAllText(Path.Combine(user.Path, ".wfx", "config.json"), """
            {
              "model": "current-model",
              "profiles": {
                "future": { "protocol": "anthropic_messages", "model": "future-model" }
              }
            }
            """);
        var current = WfxConfiguration.Load(
            workspace.Path,
            environment: new Dictionary<string, string?>(),
            userProfile: user.Path);

        var result = ModelSwitchResolver.Resolve(current, ModelSwitchRequest.Picker("1"));

        Assert.False(result.Succeeded);
        Assert.Null(result.Settings);
        Assert.Contains("not implemented yet", result.Error);
    }
}
