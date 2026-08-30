using Wfx.Core;

namespace Wfx.Cli.Tests;

[Collection("Console")]
public sealed class McpAuthCommandTests
{
    [Fact]
    public async Task Revoke_RemovesStoredCredential()
    {
        var directory = Directory.CreateTempSubdirectory("wfx-cli-tests-");
        using var httpClient = CliRunner.CreateUnexpectedHttpClient(
            "Revoking an MCP credential must not call any endpoint.");
        using var console = new ConsoleCapture();
        try
        {
            Directory.CreateDirectory(Path.Combine(directory.FullName, ".wfx"));
            var storePath = Path.Combine(directory.FullName, ".wfx", "mcp-tokens.json");
            File.WriteAllText(storePath, """
                {"servers":{"remote":{"server_url":"https://mcp.example.com/mcp","access_token":"access-1","refresh_token":"refresh-1","token_endpoint":"https://auth.example.com/token","client_id":"wfx"}}}
                """);

            var exitCode = await CliRunner.RunAsync(
                ["mcp", "auth", "--revoke", "remote"],
                httpClient,
                new SessionStore(Path.Combine(directory.FullName, "sessions")),
                TestContext.Current.CancellationToken,
                userProfile: directory.FullName);

            Assert.Equal(0, exitCode);
            Assert.DoesNotContain("access-1", File.ReadAllText(storePath));
            Assert.Contains("removed the stored credential", console.Output.ToString());
            Assert.DoesNotContain("access-1", console.Output.ToString());
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task Revoke_WithoutStoredCredential_StillSucceeds()
    {
        var directory = Directory.CreateTempSubdirectory("wfx-cli-tests-");
        using var httpClient = CliRunner.CreateUnexpectedHttpClient(
            "Revoking an MCP credential must not call any endpoint.");
        using var console = new ConsoleCapture();
        try
        {
            var exitCode = await CliRunner.RunAsync(
                ["mcp", "auth", "--revoke", "ghost"],
                httpClient,
                new SessionStore(Path.Combine(directory.FullName, "sessions")),
                TestContext.Current.CancellationToken,
                userProfile: directory.FullName);

            Assert.Equal(0, exitCode);
            Assert.Contains("no stored credential", console.Output.ToString());
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task Auth_UnknownServer_IsAUsageError()
    {
        var directory = Directory.CreateTempSubdirectory("wfx-cli-tests-");
        using var httpClient = CliRunner.CreateUnexpectedHttpClient(
            "An unknown MCP server must not call any endpoint.");
        using var console = new ConsoleCapture();
        try
        {
            var exitCode = await CliRunner.RunAsync(
                ["mcp", "auth", "ghost"],
                httpClient,
                new SessionStore(Path.Combine(directory.FullName, "sessions")),
                TestContext.Current.CancellationToken,
                userProfile: directory.FullName);

            Assert.Equal(2, exitCode);
            Assert.Contains("'ghost'", console.ErrorText);
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task Auth_StdioServer_IsRejected()
    {
        var directory = Directory.CreateTempSubdirectory("wfx-cli-tests-");
        using var httpClient = CliRunner.CreateUnexpectedHttpClient(
            "A stdio MCP server must not call any endpoint.");
        using var console = new ConsoleCapture();
        try
        {
            Directory.CreateDirectory(Path.Combine(directory.FullName, ".wfx"));
            File.WriteAllText(Path.Combine(directory.FullName, ".wfx", "config.json"), """
                { "mcp_servers": { "local": { "command": "node" } } }
                """);

            var exitCode = await CliRunner.RunAsync(
                ["mcp", "auth", "local"],
                httpClient,
                new SessionStore(Path.Combine(directory.FullName, "sessions")),
                TestContext.Current.CancellationToken,
                userProfile: directory.FullName);

            Assert.Equal(2, exitCode);
            Assert.Contains("stdio", console.ErrorText);
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }
}
