using Wfx.Mcp;

namespace Wfx.Mcp.Tests;

public sealed class McpToolNamesTests
{
    [Fact]
    public void ForTool_NamespacesServerAndTool()
    {
        Assert.Equal("mcp_echo_reverse", McpToolNames.ForTool("echo", "reverse"));
    }

    [Fact]
    public void ForTool_SanitizesUnportableCharacters()
    {
        Assert.Equal("mcp_Echo_Server_do_thing", McpToolNames.ForTool("Echo Server", "do-thing"));
    }

    [Fact]
    public void ForTool_ReplacesEmptySegments()
    {
        Assert.Equal("mcp_unnamed_unnamed", McpToolNames.ForTool("", "  "));
    }

    [Fact]
    public void ForTool_CapsLengthAt64()
    {
        var name = McpToolNames.ForTool("server", new string('x', 200));

        Assert.Equal(64, name.Length);
        Assert.StartsWith("mcp_server_", name, StringComparison.Ordinal);
    }
}
