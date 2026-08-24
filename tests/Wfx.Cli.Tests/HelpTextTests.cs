namespace Wfx.Cli.Tests;

public sealed class HelpTextTests
{
    [Fact]
    public void WrapLeavesTextShorterThanTheWidthOnOneLine()
    {
        Assert.Equal(["alpha beta"], HelpText.Wrap("alpha beta", 11));
    }

    [Fact]
    public void WrapKeepsTextExactlyAtTheWidthOnOneLine()
    {
        Assert.Equal(["alpha beta"], HelpText.Wrap("alpha beta", 10));
    }

    [Fact]
    public void WrapBreaksTextOneCharacterOverTheWidth()
    {
        Assert.Equal(["alpha", "beta"], HelpText.Wrap("alpha beta", 9));
    }

    [Fact]
    public void WrapBreaksAtWordBoundariesWithoutSplittingWords()
    {
        Assert.Equal(["alpha beta", "gamma delta"], HelpText.Wrap("alpha beta gamma delta", 11));
    }

    [Fact]
    public void WrapGivesAWordLongerThanTheWidthALineToItself()
    {
        Assert.Equal(
            ["short", "extraordinarilylongword", "end"],
            HelpText.Wrap("short extraordinarilylongword end", 10));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void WrapReturnsOneEmptyLineForBlankText(string text)
    {
        Assert.Equal([""], HelpText.Wrap(text, 80));
    }

    [Fact]
    public void WrapCollapsesRunsOfWhitespaceToSingleSpaces()
    {
        Assert.Equal(["one two three"], HelpText.Wrap("one  two\tthree", 80));
    }
}
