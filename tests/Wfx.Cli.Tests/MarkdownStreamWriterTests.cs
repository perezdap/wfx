using System.Linq;

namespace Wfx.Cli.Tests;

/// <summary>
/// The hold-back scanner (ADR 0010): styling survives arbitrary delta boundaries, and an
/// unresolved marker degrades to literal text rather than swallowing it.
/// </summary>
public sealed class MarkdownStreamWriterTests
{
    private const string Esc = "\u001b";

    [Theory]
    [InlineData("plain prose with no markers")]
    [InlineData("a path like src/Wfx.Cli/Program.cs")]
    [InlineData("snake_case_identifier and *single* stars")]
    public void UnmarkedProsePassesThroughUnchanged(string text)
    {
        Assert.Equal(text, Render(text));
    }

    [Fact]
    public void BoldIsStyled()
    {
        Assert.Equal($"a {Esc}[1mbold{Esc}[22m word", Render("a **bold** word"));
    }

    [Fact]
    public void InlineCodeIsStyled()
    {
        Assert.Equal($"call {Esc}[2mWrite(){Esc}[22m now", Render("call `Write()` now"));
    }

    /// <summary>
    /// The closed feature list does not recursively scan inline markers inside bold. This avoids
    /// introducing nested weight state merely because the bold span contains backticks.
    /// </summary>
    [Fact]
    public void InlineCodeMarkersInsideBoldRemainLiteral()
    {
        Assert.Equal(
            $"{Esc}[1mtext with `code` tail{Esc}[22m",
            Render("**text with `code` tail**"));
    }

    [Fact]
    public void AtxHeadingsBecomeBoldWithoutTheHashes()
    {
        Assert.Equal($"{Esc}[1mTitle{Esc}[22m\n", Render("## Title\n"));
    }

    [Fact]
    public void BulletMarkersAreNormalised()
    {
        Assert.Equal("• first\n• second\n", Render("- first\n* second\n"));
        Assert.Equal("- first\n", Render("- first\n", unicode: false));
    }

    [Fact]
    public void AnUnmatchedMarkerDegradesToLiteralTextAtTheLineEnd()
    {
        Assert.Equal("a `dangling backtick\n", Render("a `dangling backtick\n"));
        Assert.Equal("a **dangling bold\n", Render("a **dangling bold\n"));
    }

    [Fact]
    public void EndBlockFlushesHeldBytesAndClosesTheLine()
    {
        var output = new StringWriter();
        var writer = new MarkdownStreamWriter(output, new AnsiPalette(true), unicode: true);

        writer.Write("trailing `held");
        writer.EndBlock();

        Assert.Equal("trailing `held\n", output.ToString());
    }

    /// <summary>
    /// Suppressed decoration means the model's own text, markers and all: the markers are the
    /// model's, not WFX's, so with nothing to style there is nothing to consume either.
    /// </summary>
    [Fact]
    public void APlainPaletteIsAPassThrough()
    {
        const string source = "## Title\nsome **bold** and `code`\n- bullet\n";

        var rendered = Render(source, styled: false);

        Assert.DoesNotContain('\u001b', rendered);
        Assert.Equal(source, rendered);
    }

    [Fact]
    public void AFenceLineIsDimmedAndKeepsItsLanguageLabel()
    {
        Assert.Equal(
            $"{Esc}[2m```csharp{Esc}[22m\n",
            Render("```csharp\n"));
    }

    /// <summary>
    /// Inside a fenced block every character is code, so markdown markers must not be scanned —
    /// a C dereference or a nested backtick has to survive byte for byte.
    /// </summary>
    [Fact]
    public void AFencedBlockBodyIsNeverScanned()
    {
        var rendered = Render("```c\nint **pp; // `x` and *y*\n# not a heading\n```\nafter **bold**\n");

        Assert.Contains("int **pp; // `x` and *y*\n", rendered);
        Assert.Contains("# not a heading\n", rendered);
        Assert.Contains($"after {Esc}[1mbold{Esc}[22m\n", rendered);
    }

    /// <summary>
    /// Bold and dim share SGR 22, so a span closing inside a heading would cancel the heading's
    /// own weight and leave the rest of the line unstyled.
    /// </summary>
    [Fact]
    public void ASpanInsideAHeadingReopensTheHeadingWeight()
    {
        var rendered = Render("## A **B** C\n");

        Assert.Equal($"{Esc}[1mA {Esc}[1mB{Esc}[22m{Esc}[1m C{Esc}[22m\n", rendered);
    }

    [Fact]
    public void MoreThanSixHashesIsNotAHeading()
    {
        Assert.Equal("######## eight\n", Render("######## eight\n"));
    }

    [Fact]
    public void AdjacentBackticksAreGivenBackRatherThanBecomingAnEmptySpan()
    {
        Assert.Equal("a `` b\n", Render("a `` b\n"));
    }

    /// <summary>
    /// The property that matters: a provider may split a delta anywhere, including between the
    /// two asterisks of a bold marker, and the rendering must not change.
    /// </summary>
    [Fact]
    public void RenderingIsIndependentOfDeltaBoundaries()
    {
        const string source = "## Heading **inside**\nSome **bold** text, `code`, and a *lone* star.\n- bullet\n```csharp\nvar x = **p;\n```\ntail\n";
        var expected = Render(source);

        for (var split = 1; split < source.Length; split++)
        {
            var actual = Render(source[..split], source[split..]);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void RenderingIsIndependentOfCharacterByCharacterDelivery()
    {
        const string source = "**a** `b` ## not a heading\nreal **c**\n";

        Assert.Equal(Render(source), Render([.. source.Select(static c => c.ToString())]));
    }

    private static string Render(params string[] deltas) => Render(true, true, deltas);

    private static string Render(string text, bool styled = true, bool unicode = true) =>
        Render(styled, unicode, [text]);

    private static string Render(bool styled, bool unicode, string[] deltas)
    {
        var output = new StringWriter();
        var writer = new MarkdownStreamWriter(output, new AnsiPalette(styled), unicode);
        foreach (var delta in deltas)
        {
            writer.Write(delta);
        }

        return output.ToString();
    }
}
