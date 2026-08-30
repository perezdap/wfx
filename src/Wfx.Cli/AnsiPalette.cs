namespace Wfx.Cli;

/// <summary>
/// The four decoration styles WFX draws with, taken from the basic ANSI set so the user's
/// terminal theme resolves the hues and WFX owns no light/dark palette (ADR 0009).
/// </summary>
/// <param name="Enabled">
/// False when stderr is redirected, <c>--quiet</c> is set, or <c>NO_COLOR</c> is present, in
/// which case every style is the identity.
/// </param>
internal readonly record struct AnsiPalette(bool Enabled)
{
    private const string BoldOpenSequence = "\u001b[1m";

    private const string DimOpenSequence = "\u001b[2m";

    // Bold and dim share this closing sequence, so a caller that nests them must reopen the
    // outer weight afterwards (see MarkdownStreamWriter.WriteSpan).
    private const string WeightCloseSequence = "\u001b[22m";

    private const string RedOpenSequence = "\u001b[31m";

    private const string YellowOpenSequence = "\u001b[33m";

    private const string ColourCloseSequence = "\u001b[39m";

    public string BoldOpen => Enabled ? BoldOpenSequence : string.Empty;

    public string DimOpen => Enabled ? DimOpenSequence : string.Empty;

    public string WeightClose => Enabled ? WeightCloseSequence : string.Empty;

    public string Bold(string text) => Enabled ? BoldOpenSequence + text + WeightCloseSequence : text;

    public string Dim(string text) => Enabled ? DimOpenSequence + text + WeightCloseSequence : text;

    public string Red(string text) => Enabled ? RedOpenSequence + text + ColourCloseSequence : text;

    public string Yellow(string text) => Enabled ? YellowOpenSequence + text + ColourCloseSequence : text;
}
