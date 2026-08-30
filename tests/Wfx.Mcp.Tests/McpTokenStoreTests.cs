using Wfx.Mcp;

namespace Wfx.Mcp.Tests;

public sealed class McpTokenStoreTests
{
    [Fact]
    public void Get_MissingFile_ReturnsNull()
    {
        using var directory = new TemporaryDirectory();
        var store = new McpTokenStore(Path.Combine(directory.Path, "mcp-tokens.json"));

        Assert.Null(store.Get("remote"));
    }

    [Fact]
    public void Save_ThenGet_RoundTrips()
    {
        using var directory = new TemporaryDirectory();
        var store = new McpTokenStore(Path.Combine(directory.Path, "mcp-tokens.json"));
        var record = new McpTokenRecord(
            "https://mcp.example.com/mcp",
            "access-1",
            "refresh-1",
            DateTimeOffset.Parse("2026-08-29T20:00:00Z"),
            "https://auth.example.com/token",
            "client-1");

        store.Save("remote", record);
        var loaded = store.Get("remote");

        Assert.Equal(record, loaded);
    }

    [Fact]
    public void Save_ReplacesExistingEntry_AndKeepsOtherServers()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "mcp-tokens.json");
        var store = new McpTokenStore(path);
        store.Save("a", new McpTokenRecord("https://a.example.com/mcp", "at-a", null, null, "https://a.example.com/token", "wfx"));
        store.Save("b", new McpTokenRecord("https://b.example.com/mcp", "at-b", null, null, "https://b.example.com/token", "wfx"));
        store.Save("a", new McpTokenRecord("https://a.example.com/mcp", "at-a2", null, null, "https://a.example.com/token", "wfx"));

        Assert.Equal("at-a2", store.Get("a")!.AccessToken);
        Assert.Equal("at-b", store.Get("b")!.AccessToken);
    }

    [Fact]
    public void Remove_DropsStoredCredential()
    {
        using var directory = new TemporaryDirectory();
        var store = new McpTokenStore(Path.Combine(directory.Path, "mcp-tokens.json"));
        store.Save("remote", new McpTokenRecord("https://mcp.example.com/mcp", "at", "rt", null, "https://auth.example.com/token", "wfx"));

        Assert.True(store.Remove("remote"));
        Assert.Null(store.Get("remote"));
        Assert.False(store.Remove("remote"));
    }

    [Fact]
    public void Get_CorruptFile_ReturnsNull()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "mcp-tokens.json");
        File.WriteAllText(path, "not json at all {{{");
        var store = new McpTokenStore(path);

        Assert.Null(store.Get("remote"));
    }

    [Fact]
    public void ForUserProfile_StoresUnderDotWfx()
    {
        var store = McpTokenStore.ForUserProfile(@"C:\Users\test");

        Assert.Equal(Path.Combine(@"C:\Users\test", ".wfx", "mcp-tokens.json"), store.Path);
    }
}
