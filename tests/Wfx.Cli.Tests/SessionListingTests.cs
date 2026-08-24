using Wfx.Core;

namespace Wfx.Cli.Tests;

[Collection("Console")]
public sealed class SessionListingTests
{
    [Fact]
    public async Task SessionsReportsAnEmptyStoreWithoutModelConfiguration()
    {
        var directory = Directory.CreateTempSubdirectory("wfx-cli-tests-");
        using var httpClient = CliRunner.CreateUnexpectedHttpClient(
            "The sessions command must not call a model endpoint.");
        using var console = new ConsoleCapture();
        try
        {
            var store = new SessionStore(Path.Combine(directory.FullName, "sessions"));
            var exitCode = await CliRunner.RunAsync(
                ["sessions"],
                httpClient,
                store,
                TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            Assert.Contains("No sessions.", console.Output.ToString());
            Assert.Contains("Total on disk: 0 B", console.Output.ToString());
            Assert.Empty(console.ErrorText);
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task SessionsPrintsRealStoreWorkspaceTimestampsSizeAndTotal()
    {
        var directory = Directory.CreateTempSubdirectory("wfx-cli-tests-");
        using var httpClient = CliRunner.CreateUnexpectedHttpClient(
            "The sessions command must not call a model endpoint.");
        using var console = new ConsoleCapture();
        try
        {
            var store = new SessionStore(Path.Combine(directory.FullName, "sessions"));
            const string workspace = @"C:\wfx";
            using (store.Create(workspace))
            {
            }

            var listing = store.List();
            var summary = Assert.Single(listing.Sessions);
            Assert.NotNull(summary.CreatedAt);
            Assert.InRange(summary.SizeBytes, 1, 1023);

            var exitCode = await CliRunner.RunAsync(
                ["sessions"],
                httpClient,
                store,
                TestContext.Current.CancellationToken);

            var output = console.Output.ToString();
            Assert.Equal(0, exitCode);
            Assert.Contains("SESSION", output);
            Assert.Contains("WORKSPACE", output);
            Assert.Contains(summary.SessionId, output);
            Assert.Contains(workspace, output);
            Assert.Contains(summary.CreatedAt.Value.ToString("yyyy-MM-dd HH:mm:ss"), output);
            Assert.Contains(summary.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss"), output);
            Assert.Contains($"{summary.SizeBytes} B", output);
            Assert.Contains($"1 session(s), {listing.TotalSizeBytes} B total on disk", output);
            Assert.Empty(console.ErrorText);
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task HelpDocumentsTheSessionsCommand()
    {
        using var httpClient = CliRunner.CreateUnexpectedHttpClient(
            "The sessions command must not call a model endpoint.");
        using var console = new ConsoleCapture();

        var exitCode = await CliRunner.RunAsync(
            ["--help"],
            httpClient,
            new TestSessionStore(),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("wfx sessions [options]", console.Output.ToString());
        Assert.Contains("sizes, and total", console.Output.ToString());
        Assert.Empty(console.ErrorText);
    }
}
