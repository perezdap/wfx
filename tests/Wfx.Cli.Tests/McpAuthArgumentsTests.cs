namespace Wfx.Cli.Tests;

public sealed class McpAuthArgumentsTests
{
    [Fact]
    public void ParsesMcpAuthWithServerName()
    {
        var arguments = CliArguments.Parse(["mcp", "auth", "remote"]);

        Assert.Equal(CliCommand.McpAuth, arguments.Command);
        Assert.Equal("remote", arguments.McpServerName);
        Assert.False(arguments.McpRevoke);
    }

    [Fact]
    public void ParsesMcpAuthRevoke()
    {
        var arguments = CliArguments.Parse(["mcp", "auth", "--revoke", "remote"]);

        Assert.Equal(CliCommand.McpAuth, arguments.Command);
        Assert.Equal("remote", arguments.McpServerName);
        Assert.True(arguments.McpRevoke);
    }

    [Fact]
    public void RejectsMcpWithoutAuthSubcommand()
    {
        var exception = Assert.Throws<ArgumentException>(() => CliArguments.Parse(["mcp", "remote"]));

        Assert.Contains("wfx mcp auth", exception.Message);
    }

    [Fact]
    public void RejectsMcpAuthWithoutServerName()
    {
        var exception = Assert.Throws<ArgumentException>(() => CliArguments.Parse(["mcp", "auth"]));

        Assert.Contains("wfx mcp auth", exception.Message);
    }

    [Fact]
    public void RejectsRevokeOutsideMcpAuth()
    {
        Assert.Throws<ArgumentException>(() => CliArguments.Parse(["run", "--revoke", "do it"]));
    }

    [Fact]
    public void RejectsJsonWithMcpAuth()
    {
        Assert.Throws<ArgumentException>(() => CliArguments.Parse(["mcp", "auth", "remote", "--json"]));
    }
}
