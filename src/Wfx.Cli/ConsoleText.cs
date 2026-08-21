namespace Wfx.Cli;

/// <summary>
/// Adapts console text to what the current output encoding can actually render.
/// </summary>
internal static class ConsoleText
{
    public const string Marker = "●";

    public const string AsciiMarker = "*";

    public const string Ellipsis = "…";

    public const string AsciiEllipsis = "...";

    public static string ForConsole(string value, bool unicode) =>
        unicode ? value : value.Replace(Ellipsis, AsciiEllipsis, StringComparison.Ordinal);
}
