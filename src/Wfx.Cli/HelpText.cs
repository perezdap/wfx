using System.Text;

namespace Wfx.Cli;

/// <summary>
/// Presentation helpers for the console help text. Canonical wording stays in a single string
/// per topic; these helpers render it for the help layout without duplicating it.
/// </summary>
internal static class HelpText
{
    /// <summary>
    /// Greedily wraps <paramref name="text"/> at word boundaries so no line is wider than
    /// <paramref name="maxWidth"/>. A single word longer than the width gets a line to itself.
    /// Empty or whitespace-only text yields one empty line.
    /// </summary>
    public static IReadOnlyList<string> Wrap(string text, int maxWidth)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>();
        var current = new StringBuilder();
        foreach (var word in words)
        {
            if (current.Length == 0)
            {
                current.Append(word);
            }
            else if (current.Length + 1 + word.Length <= maxWidth)
            {
                current.Append(' ').Append(word);
            }
            else
            {
                lines.Add(current.ToString());
                current.Clear().Append(word);
            }
        }

        lines.Add(current.ToString());
        return lines;
    }
}
