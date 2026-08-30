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
    public void Load_ParsesHttpMcpServer()
    {
        using var workspace = new TemporaryDirectory();
        using var profile = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(profile.Path, ".wfx"));
        File.WriteAllText(Path.Combine(profile.Path, ".wfx", "config.json"), """
            {
              "mcp_servers": {
                "remote": {
                  "url": "https://mcp.example.com/mcp",
                  "headers": { "X-Api-Key": "secret-value" }
                }
              }
            }
            """);

        var result = WfxConfiguration.Load(
            workspace.Path,
            environment: new Dictionary<string, string?>(),
            userProfile: profile.Path);

        var server = Assert.Single(result.McpServers);
        Assert.Equal("remote", server.Key, StringComparer.OrdinalIgnoreCase);
        Assert.Null(server.Value.Command);
        Assert.Equal("https://mcp.example.com/mcp", server.Value.Url);
        Assert.True(server.Value.IsHttp);
        Assert.Equal("secret-value", server.Value.Headers["X-Api-Key"]);
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
    public void Load_RejectsMcpServerWithBothCommandAndUrl()
    {
        using var workspace = new TemporaryDirectory();
        using var profile = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(profile.Path, ".wfx"));
        File.WriteAllText(Path.Combine(profile.Path, ".wfx", "config.json"), """
            { "mcp_servers": { "echo": { "command": "node", "url": "https://mcp.example.com/mcp" } } }
            """);

        var exception = Assert.Throws<InvalidOperationException>(() => WfxConfiguration.Load(
            workspace.Path,
            environment: new Dictionary<string, string?>(),
            userProfile: profile.Path));

        Assert.Contains("'echo'", exception.Message);
        Assert.Contains("'command'", exception.Message);
        Assert.Contains("'url'", exception.Message);
        Assert.Contains("exactly one transport", exception.Message);
        Assert.Contains("both", exception.Message);
    }

    [Fact]
    public void Load_RejectsMcpServerWithNeitherCommandNorUrl()
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
        Assert.Contains("'command'", exception.Message);
        Assert.Contains("'url'", exception.Message);
        Assert.Contains("neither", exception.Message);
    }

    [Fact]
    public void Load_RejectsNonAbsoluteMcpUrl()
    {
        using var workspace = new TemporaryDirectory();
        using var profile = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(profile.Path, ".wfx"));
        File.WriteAllText(Path.Combine(profile.Path, ".wfx", "config.json"), """
            { "mcp_servers": { "echo": { "url": "mcp.example.com/mcp" } } }
            """);

        var exception = Assert.Throws<InvalidOperationException>(() => WfxConfiguration.Load(
            workspace.Path,
            environment: new Dictionary<string, string?>(),
            userProfile: profile.Path));

        Assert.Contains("'echo'", exception.Message);
        Assert.Contains("'url'", exception.Message);
        Assert.Contains("absolute", exception.Message);
    }

    [Fact]
    public void Load_RejectsNonHttpMcpUrlScheme()
    {
        using var workspace = new TemporaryDirectory();
        using var profile = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(profile.Path, ".wfx"));
        File.WriteAllText(Path.Combine(profile.Path, ".wfx", "config.json"), """
            { "mcp_servers": { "echo": { "url": "ftp://mcp.example.com/mcp" } } }
            """);

        var exception = Assert.Throws<InvalidOperationException>(() => WfxConfiguration.Load(
            workspace.Path,
            environment: new Dictionary<string, string?>(),
            userProfile: profile.Path));

        Assert.Contains("http", exception.Message);
    }

    [Fact]
    public void Load_RejectsNonStringMcpHeaderValues()
    {
        using var workspace = new TemporaryDirectory();
        using var profile = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(profile.Path, ".wfx"));
        File.WriteAllText(Path.Combine(profile.Path, ".wfx", "config.json"), """
            { "mcp_servers": { "echo": { "url": "https://mcp.example.com/mcp", "headers": { "X-Api-Key": 42 } } } }
            """);

        var exception = Assert.Throws<InvalidOperationException>(() => WfxConfiguration.Load(
            workspace.Path,
            environment: new Dictionary<string, string?>(),
            userProfile: profile.Path));

        Assert.Contains("'X-Api-Key'", exception.Message);
    }

    [Fact]
    public void Load_RejectsStdioKeysOnHttpServer()
    {
        using var workspace = new TemporaryDirectory();
        using var profile = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(profile.Path, ".wfx"));
        File.WriteAllText(Path.Combine(profile.Path, ".wfx", "config.json"), """
            { "mcp_servers": { "echo": { "url": "https://mcp.example.com/mcp", "args": ["server.js"] } } }
            """);

        var exception = Assert.Throws<InvalidOperationException>(() => WfxConfiguration.Load(
            workspace.Path,
            environment: new Dictionary<string, string?>(),
            userProfile: profile.Path));

        Assert.Contains("'echo'", exception.Message);
        Assert.Contains("'args'", exception.Message);
    }

    [Fact]
    public void Load_RejectsHeadersOnStdioServer()
    {
        using var workspace = new TemporaryDirectory();
        using var profile = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(profile.Path, ".wfx"));
        File.WriteAllText(Path.Combine(profile.Path, ".wfx", "config.json"), """
            { "mcp_servers": { "echo": { "command": "node", "headers": { "X-Api-Key": "x" } } } }
            """);

        var exception = Assert.Throws<InvalidOperationException>(() => WfxConfiguration.Load(
            workspace.Path,
            environment: new Dictionary<string, string?>(),
            userProfile: profile.Path));

        Assert.Contains("'echo'", exception.Message);
        Assert.Contains("'headers'", exception.Message);
    }

    [Fact]
    public void LoadUserMcpServers_ReadsUserLayerOnly()
    {
        using var workspace = new TemporaryDirectory();
        using var profile = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(profile.Path, ".wfx"));
        File.WriteAllText(Path.Combine(profile.Path, ".wfx", "config.json"), """
            { "mcp_servers": { "remote": { "url": "https://mcp.example.com/mcp" } } }
            """);

        var servers = WfxConfiguration.LoadUserMcpServers(profile.Path);

        Assert.Single(servers);
        Assert.Equal("https://mcp.example.com/mcp", servers["remote"].Url);
    }

    [Fact]
    public void LoadUserMcpServers_MissingConfig_ReturnsEmpty()
    {
        using var profile = new TemporaryDirectory();

        Assert.Empty(WfxConfiguration.LoadUserMcpServers(profile.Path));
    }

    [Fact]
    public void LoadUserMcpServers_WithProfile_PrefersProfileMap()
    {
        using var profile = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(profile.Path, ".wfx"));
        File.WriteAllText(Path.Combine(profile.Path, ".wfx", "config.json"), """
            {
              "mcp_servers": { "base": { "command": "node" } },
              "profiles": {
                "dev": { "mcp_servers": { "remote": { "url": "https://mcp.example.com/mcp" } } }
              }
            }
            """);

        var servers = WfxConfiguration.LoadUserMcpServers(profile.Path, "dev");

        var server = Assert.Single(servers);
        Assert.Equal("remote", server.Key);
    }

    [Fact]
    public void LoadUserMcpServers_UnknownProfile_Throws()
    {
        using var profile = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(profile.Path, ".wfx"));
        File.WriteAllText(Path.Combine(profile.Path, ".wfx", "config.json"), """
            { "mcp_servers": { "base": { "command": "node" } } }
            """);

        var exception = Assert.Throws<InvalidOperationException>(
            () => WfxConfiguration.LoadUserMcpServers(profile.Path, "ghost"));

        Assert.Contains("'ghost'", exception.Message);
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

    [Fact]
    public void Load_RejectsMalformedProjectMcpServersWithTheTrustBoundaryMessage()
    {
        using var workspace = new TemporaryDirectory();
        using var profile = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(workspace.Path, ".wfx"));
        File.WriteAllText(Path.Combine(workspace.Path, ".wfx", "config.json"), """
            { "mcp_servers": "not-even-an-object" }
            """);

        var exception = Assert.Throws<InvalidOperationException>(() => WfxConfiguration.Load(
            workspace.Path,
            environment: new Dictionary<string, string?>(),
            userProfile: profile.Path));

        Assert.Contains("'mcp_servers'", exception.Message);
        Assert.Contains("user configuration", exception.Message);
    }

    [Fact]
    public void Load_RejectsMalformedProjectProfileMcpServersWithTheTrustBoundaryMessage()
    {
        using var workspace = new TemporaryDirectory();
        using var profile = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(workspace.Path, ".wfx"));
        File.WriteAllText(Path.Combine(workspace.Path, ".wfx", "config.json"), """
            { "profiles": { "dev": { "mcp_servers": [1, 2] } } }
            """);

        var exception = Assert.Throws<InvalidOperationException>(() => WfxConfiguration.Load(
            workspace.Path,
            environment: new Dictionary<string, string?>(),
            userProfile: profile.Path));

        Assert.Contains("'mcp_servers'", exception.Message);
        Assert.Contains("user configuration", exception.Message);
    }
}
