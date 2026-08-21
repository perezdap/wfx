using Wfx.Core;

namespace Wfx.Core.Tests;

public sealed class ProtocolConfigurationTests
{
    [Fact]
    public void Load_DefaultProtocolIsChatCompletions()
    {
        using var workspace = new TemporaryDirectory();

        var result = WfxConfiguration.Load(
            workspace.Path,
            new WfxSettingsLayer { Model = "gpt-5" },
            new Dictionary<string, string?>(),
            Path.Combine(workspace.Path, "missing-profile"));

        Assert.Equal("chat_completions", result.Protocol);
    }

    [Fact]
    public void Load_ResolvesProtocolFromUserConfigFile()
    {
        using var workspace = new TemporaryDirectory();
        using var profile = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(profile.Path, ".wfx"));
        File.WriteAllText(Path.Combine(profile.Path, ".wfx", "config.json"), """
            { "protocol": "responses", "model": "gpt-5" }
            """);

        var result = WfxConfiguration.Load(
            workspace.Path,
            environment: new Dictionary<string, string?>(),
            userProfile: profile.Path);

        Assert.Equal("responses", result.Protocol);
        Assert.Equal("gpt-5", result.Model);
    }

    [Fact]
    public void Load_ResolvesProtocolFromConfigFile()
    {
        using var workspace = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(workspace.Path, ".wfx"));
        File.WriteAllText(Path.Combine(workspace.Path, ".wfx", "config.json"), """
            { "protocol": "responses", "model": "gpt-5" }
            """);

        var result = WfxConfiguration.Load(
            workspace.Path,
            environment: new Dictionary<string, string?>(),
            userProfile: Path.Combine(workspace.Path, "missing-profile"));

        Assert.Equal("responses", result.Protocol);
    }

    [Fact]
    public void Load_ResolvesProtocolFromEnvironment()
    {
        using var workspace = new TemporaryDirectory();

        var result = WfxConfiguration.Load(
            workspace.Path,
            environment: new Dictionary<string, string?> { ["WFX_PROTOCOL"] = "responses" },
            userProfile: Path.Combine(workspace.Path, "missing-profile"));

        Assert.Equal("responses", result.Protocol);
    }

    [Fact]
    public void Load_CliProtocolOverridesEnvironment()
    {
        using var workspace = new TemporaryDirectory();

        var result = WfxConfiguration.Load(
            workspace.Path,
            new WfxSettingsLayer { Protocol = "responses", Model = "gpt-5" },
            new Dictionary<string, string?> { ["WFX_PROTOCOL"] = "chat_completions" },
            Path.Combine(workspace.Path, "missing-profile"));

        Assert.Equal("responses", result.Protocol);
    }

    [Fact]
    public void Load_AnthropicMessagesFailsWithExplicitNotImplementedError()
    {
        using var workspace = new TemporaryDirectory();

        var exception = Assert.Throws<InvalidOperationException>(() => WfxConfiguration.Load(
            workspace.Path,
            new WfxSettingsLayer { Protocol = "anthropic_messages", Model = "claude-sonnet-4-6" },
            new Dictionary<string, string?>(),
            Path.Combine(workspace.Path, "missing-profile")));

        Assert.Contains("anthropic_messages", exception.Message);
        Assert.Contains("not implemented yet", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_UnknownProtocolFailsWithValidValues()
    {
        using var workspace = new TemporaryDirectory();

        var exception = Assert.Throws<InvalidOperationException>(() => WfxConfiguration.Load(
            workspace.Path,
            new WfxSettingsLayer { Protocol = "grpc", Model = "gpt-5" },
            new Dictionary<string, string?>(),
            Path.Combine(workspace.Path, "missing-profile")));

        Assert.Contains("grpc", exception.Message);
        Assert.Contains("chat_completions", exception.Message);
        Assert.Contains("responses", exception.Message);
        Assert.Contains("anthropic_messages", exception.Message);
    }

    [Fact]
    public void Load_AnthropicProviderUsesOpenAiCompatibleEndpointAndCredential()
    {
        using var workspace = new TemporaryDirectory();

        var result = WfxConfiguration.Load(
            workspace.Path,
            new WfxSettingsLayer { Provider = "anthropic", Model = "claude-sonnet-4-6" },
            new Dictionary<string, string?> { ["ANTHROPIC_API_KEY"] = "anthropic-secret" },
            Path.Combine(workspace.Path, "missing-profile"));

        Assert.Equal("anthropic", result.Provider);
        Assert.Equal(new Uri("https://api.anthropic.com/v1"), result.BaseUri);
        Assert.Equal("anthropic-secret", result.ApiKey);
        Assert.Equal("chat_completions", result.Protocol);
    }

    [Fact]
    public void Load_ResponsesProtocolUsesOpenAiDefaultEndpoint()
    {
        using var workspace = new TemporaryDirectory();

        var result = WfxConfiguration.Load(
            workspace.Path,
            new WfxSettingsLayer { Protocol = "responses", Model = "gpt-5" },
            new Dictionary<string, string?> { ["OPENAI_API_KEY"] = "openai-secret" },
            Path.Combine(workspace.Path, "missing-profile"));

        Assert.Equal("responses", result.Protocol);
        Assert.Equal(new Uri("https://api.openai.com/v1"), result.BaseUri);
        Assert.Equal("openai-secret", result.ApiKey);
    }

    [Fact]
    public void Load_ProfileCanSetProtocol()
    {
        using var workspace = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(workspace.Path, ".wfx"));
        File.WriteAllText(Path.Combine(workspace.Path, ".wfx", "config.json"), """
            {
              "profiles": {
                "cloud": {
                  "protocol": "responses",
                  "model": "gpt-5"
                }
              }
            }
            """);

        var result = WfxConfiguration.Load(
            workspace.Path,
            new WfxSettingsLayer { Profile = "cloud" },
            new Dictionary<string, string?>(),
            Path.Combine(workspace.Path, "missing-profile"));

        Assert.Equal("responses", result.Protocol);
        Assert.Equal("gpt-5", result.Model);
    }

    [Fact]
    public void ParseModelShorthand_OpenRouterShorthandIsUnchanged()
    {
        var result = WfxConfiguration.ParseModelShorthand("openrouter/anthropic/claude-sonnet-4.6");

        Assert.Equal("openrouter", result.Provider);
        Assert.Equal("anthropic/claude-sonnet-4.6", result.Model);
        Assert.Null(result.Protocol);
    }
}
