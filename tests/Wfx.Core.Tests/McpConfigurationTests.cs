using Wfx.Core;

namespace Wfx.Core.Tests;

public sealed class McpConfigurationTests
{
    [Fact]
    public void Load_ParsesUserMcpServers()
    {
        using var workspace = new TemporaryDirectory();
        using var profile = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(profile.Path, ".wfx"));
        File.WriteAllText(Path.Combine(profile.Path, ".wfx", "config.json"), """
            {
              "mcp_servers": {
                "echo": {
                  "command": "node",
                  "args": ["server.js", "--stdio"],
                  "env": { "MCP_MODE": "stdio" }
                }
              }
            }
            """);

        var result = WfxConfiguration.Load(
            workspace.Path,
            environment: new Dictionary<string, string?>(),
            userProfile: profile.Path);

        var server = Assert.Single(result.McpServers);
        Assert.Equal("echo", server.Key, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("node", server.Value.Command);
        Assert.Equal(new[] { "server.js", "--stdio" }, server.Value.Arguments);
        Assert.Equal("stdio", server.Value.Environment["MCP_MODE"]);

        var userSource = Assert.Single(result.Sources, source => source.Layer == "user");
        Assert.Contains("mcp_servers", userSource.Keys);
    }

    [Fact]
    public void Load_WithoutMcpServers_ExposesEmptyMap()
    {
        using var workspace = new TemporaryDirectory();

        var result = WfxConfiguration.Load(
            workspace.Path,
            environment: new Dictionary<string, string?>(),
            userProfile: Path.Combine(workspace.Path, "missing-profile"));

        Assert.Empty(result.McpServers);
    }

    [Fact]
    public void Load_RejectsProjectMcpServers()
    {
        using var workspace = new TemporaryDirectory();
        using var profile = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(workspace.Path, ".wfx"));
        File.WriteAllText(Path.Combine(workspace.Path, ".wfx", "config.json"), """
            { "mcp_servers": { "echo": { "command": "node" } } }
            """);

        var exception = Assert.Throws<InvalidOperationException>(() => WfxConfiguration.Load(
            workspace.Path,
            environment: new Dictionary<string, string?>(),
            userProfile: profile.Path));

        Assert.Contains("'mcp_servers'", exception.Message);
        Assert.Contains("user configuration", exception.Message);
        Assert.Contains(Path.Combine(workspace.Path, ".wfx", "config.json"), exception.Message);
    }

    [Fact]
    public void Load_RejectsProjectProfileMcpServers()
    {
        using var workspace = new TemporaryDirectory();
        using var profile = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(workspace.Path, ".wfx"));
        File.WriteAllText(Path.Combine(workspace.Path, ".wfx", "config.json"), """
            { "profiles": { "dev": { "mcp_servers": { "echo": { "command": "node" } } } } }
            """);

        var exception = Assert.Throws<InvalidOperationException>(() => WfxConfiguration.Load(
            workspace.Path,
            environment: new Dictionary<string, string?>(),
            userProfile: profile.Path));

        Assert.Contains("'mcp_servers'", exception.Message);
    }

    [Fact]
    public void Load_AllowsMcpServersWhenUserAndProjectConfigAreTheSameFile()
    {
        using var workspace = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(workspace.Path, ".wfx"));
        File.WriteAllText(Path.Combine(workspace.Path, ".wfx", "config.json"), """
            { "mcp_servers": { "echo": { "command": "node" } } }
            """);

        var result = WfxConfiguration.Load(
            workspace.Path,
            environment: new Dictionary<string, string?>(),
            userProfile: workspace.Path);

        Assert.Single(result.McpServers);
    }

    [Fact]
    public void Load_McpServersFromSelectedUserProfile()
    {
        using var workspace = new TemporaryDirectory();
        using var profile = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(profile.Path, ".wfx"));
        File.WriteAllText(Path.Combine(profile.Path, ".wfx", "config.json"), """
            { "profiles": { "dev": { "mcp_servers": { "echo": { "command": "node" } } } } }
            """);

        var result = WfxConfiguration.Load(
            workspace.Path,
            new WfxSettingsLayer { Profile = "dev" },
            new Dictionary<string, string?>(),
            profile.Path);

        Assert.Single(result.McpServers);
    }

    [Fact]
    public void Load_RejectsMcpServerWithoutCommand()
    {
        using var workspace = new TemporaryDirectory();
        using var profile = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(profile.Path, ".wfx"));
        File.WriteAllText(Path.Combine(profile.Path, ".wfx", "config.json"), """
            { "mcp_servers": { "echo": { "args": ["server.js"] } } }
            """);

        var exception = Assert.Throws<InvalidOperationException>(() => WfxConfiguration.Load(
            workspace.Path,
            environment: new Dictionary<string, string?>(),
            userProfile: profile.Path));

        Assert.Contains("'echo'", exception.Message);
        Assert.Contains("command", exception.Message);
    }

    [Fact]
    public void Load_RejectsNonStringMcpEnvironmentValues()
    {
        using var workspace = new TemporaryDirectory();
        using var profile = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(profile.Path, ".wfx"));
        File.WriteAllText(Path.Combine(profile.Path, ".wfx", "config.json"), """
            { "mcp_servers": { "echo": { "command": "node", "env": { "PORT": 8080 } } } }
            """);

        var exception = Assert.Throws<InvalidOperationException>(() => WfxConfiguration.Load(
            workspace.Path,
            environment: new Dictionary<string, string?>(),
            userProfile: profile.Path));

        Assert.Contains("'PORT'", exception.Message);
    }

    [Fact]
    public void Load_RejectsNonStringMcpArguments()
    {
        using var workspace = new TemporaryDirectory();
        using var profile = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(profile.Path, ".wfx"));
        File.WriteAllText(Path.Combine(profile.Path, ".wfx", "config.json"), """
            { "mcp_servers": { "echo": { "command": "node", "args": [1] } } }
            """);

        var exception = Assert.Throws<InvalidOperationException>(() => WfxConfiguration.Load(
            workspace.Path,
            environment: new Dictionary<string, string?>(),
            userProfile: profile.Path));

        Assert.Contains("args", exception.Message);
    }
}
