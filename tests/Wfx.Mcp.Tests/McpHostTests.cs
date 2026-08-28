using Wfx.Core;
using Wfx.Mcp;

namespace Wfx.Mcp.Tests;

public sealed class McpHostTests
{
    [Fact]
    public async Task ConnectAsync_UnavailableServer_WarnsAndContributesNoTools()
    {
        using var workspace = new TemporaryDirectory();
        var warnings = new List<string>();
        var servers = new Dictionary<string, McpServerSettings>
        {
            ["missing"] = new("no-such-command-wfx.exe", [], new Dictionary<string, string>())
        };

        await using var host = await McpHost.ConnectAsync(servers, workspace.Path, warnings.Add, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Empty(host.Tools);
        var warning = Assert.Single(warnings);
        Assert.Contains("'missing'", warning);
    }

    [Fact]
    public async Task Assemble_KeepsFirstTool_WhenSanitizedNamesCollide()
    {
        var warnings = new List<string>();
        var clientA = CreateIdleClient();
        var clientB = CreateIdleClient();
        var clientC = CreateIdleClient();
        var clientD = CreateIdleClient();

        await using var host = McpHost.Assemble(
        [
            ("alpha", clientA, [new McpToolInfo("echo", null, null)]),
            ("gam-ma", clientB, [new McpToolInfo("ping", null, null)]),
            // Sanitizes to the same name as 'gam-ma' and must lose.
            ("gam_ma", clientC, [new McpToolInfo("ping", null, null)]),
            // The same server listing the same tool twice.
            ("delta", clientD, [new McpToolInfo("ping", null, null), new McpToolInfo("ping", null, null)])
        ],
            warnings.Add);

        Assert.Equal(3, host.Tools.Count);
        Assert.Equal("mcp_alpha_echo", host.Tools[0].Definition.Name);
        Assert.Equal("mcp_gam_ma_ping", host.Tools[1].Definition.Name);
        Assert.Equal("mcp_delta_ping", host.Tools[2].Definition.Name);
        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, warning => warning.Contains("'gam_ma'"));
        Assert.Contains(warnings, warning => warning.Contains("'delta'"));
    }

    [Fact]
    public async Task Assemble_CollisionFromSanitizationIsStillDetected()
    {
        var warnings = new List<string>();
        var clientA = CreateIdleClient();
        var clientB = CreateIdleClient();

        await using var host = McpHost.Assemble(
        [
            ("a-b", clientA, [new McpToolInfo("c", null, null)]),
            ("a_b", clientB, [new McpToolInfo("c", null, null)])
        ],
            warnings.Add);

        var tool = Assert.Single(host.Tools);
        Assert.Equal("mcp_a_b_c", tool.Definition.Name);
        Assert.Single(warnings);
    }

    private static McpStdioClient CreateIdleClient()
    {
        var session = new McpJsonRpcSession(TextWriter.Null, new StringReader(string.Empty));
        return new McpStdioClient(session, new RecordingDisposable());
    }
}
