using Wfx.Core;

namespace Wfx.Core.Tests;

public sealed class ToolCallSummaryTests
{
    [Fact]
    public void ShowsScalarArgumentsInSchemaOrder()
    {
        var summary = ToolCallSummary.Describe("read_file", "{\"path\":\"src/Wfx.Core/Agent.cs\",\"max_bytes\":2048}");

        Assert.Equal("read_file(path: src/Wfx.Core/Agent.cs, max_bytes: 2048)", summary);
    }

    [Fact]
    public void CollapsesMultiLineCommandsOntoOneLine()
    {
        var summary = ToolCallSummary.Describe("powershell", "{\"script\":\"curl.exe -s \\n  https://example.invalid\"}");

        Assert.Equal("powershell(script: curl.exe -s https://example.invalid)", summary);
    }

    [Fact]
    public void SummarizesNonScalarValues()
    {
        var summary = ToolCallSummary.Describe("git", "{\"args\":[\"log\",\"-3\"],\"env\":{\"PAGER\":\"cat\"},\"quiet\":true}");

        Assert.Equal("git(args: [2 items], env: {…}, quiet: true)", summary);
    }

    [Fact]
    public void SkipsNullAndEmptyValues()
    {
        var summary = ToolCallSummary.Describe("search_files", "{\"pattern\":\"*.cs\",\"path\":\"\",\"exclude\":null}");

        Assert.Equal("search_files(pattern: *.cs)", summary);
    }

    [Fact]
    public void FallsBackToToolNameWhenArgumentsAreEmpty()
    {
        Assert.Equal("list_directory", ToolCallSummary.Describe("list_directory", "{}"));
        Assert.Equal("list_directory", ToolCallSummary.Describe("list_directory", ""));
        Assert.Equal("list_directory", ToolCallSummary.Describe("list_directory", null));
    }

    [Fact]
    public void ShowsRawArgumentsWhenJsonIsInvalid()
    {
        var summary = ToolCallSummary.Describe("powershell", "{\"script\":");

        Assert.Equal("powershell({\"script\":)", summary);
    }

    [Fact]
    public void TruncatesLongArgumentsToTheRequestedLength()
    {
        var script = new string('a', 500);
        var summary = ToolCallSummary.Describe("powershell", $"{{\"script\":\"{script}\"}}", 40);

        Assert.Equal(40 + 1, summary.Length - "powershell()".Length);
        Assert.EndsWith("…)", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeTextCollapsesAndTruncates()
    {
        Assert.Equal("failed to run", ToolCallSummary.DescribeText("  failed\r\n  to run  "));
        Assert.Equal("fail…", ToolCallSummary.DescribeText("failed to run", 4));
    }
}
