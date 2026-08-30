using System.Text;

namespace Wfx.Cli;

/// <summary>
/// Streams the model's markdown to a writer, styling it as it arrives (ADR 0010).
/// </summary>
/// <remarks>
/// Bytes pass straight through except while an inline marker is unresolved: the writer holds
/// from the marker until it closes or the line ends, so token-by-token streaming survives and
/// an unmatched marker degrades to the literal text the model sent. Line-level constructs are
/// decided from the first few characters of a line, so they cost no latency either.
/// <para>
/// The feature list is closed at bold, inline code, ATX headings, bullet markers, and dimming
/// a fence line. Adding to it reopens ADR 0010.
/// </para>
/// <para>
/// When decoration is suppressed the writer is a pass-through: markers are the model's text,
/// not WFX's, so with nothing to style there is nothing to consume either.
/// </para>
/// </remarks>
internal sealed class MarkdownStreamWriter(TextWriter output, AnsiPalette palette, bool unicode)
{
    /// <summary>Longest line prefix that can still turn out to be a heading, bullet, or fence.</summary>
    private const int MaxPrefixLength = 8;

    private const int MaxHeadingLevel = 6;

    private const string Fence = "```";

    private readonly StringBuilder _prefix = new();

    private readonly StringBuilder _held = new();

    private Hold _hold = Hold.None;

    private bool _decidingPrefix = true;

    private bool _headingOpen;

    private bool _fenceLineOpen;

    /// <summary>Set on a fence line, applied at its end, so the fence line itself still renders.</summary>
    private bool _fenceToggled;

    /// <summary>Inside a fenced block: every character is code, so nothing is scanned.</summary>
    private bool _inFence;

    /// <summary>The rest of this line is literal — the language label after a fence marker.</summary>
    private bool _literalRest;

    private bool _atLineStart = true;

    private enum Hold
    {
        None,

        /// <summary>One asterisk seen; a second makes it bold, anything else makes it literal.</summary>
        MaybeBold,

        Bold,

        /// <summary>Inside bold, one closing asterisk seen.</summary>
        MaybeBoldClose,

        Code
    }

    public void Write(string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (!palette.Enabled)
        {
            output.Write(text);
            _atLineStart = text[^1] == '\n';
            return;
        }

        foreach (var character in text)
        {
            Process(character);
        }
    }

    /// <summary>
    /// Ends the prose block: anything still held is written literally and the line is closed, so
    /// whatever comes next starts at column zero.
    /// </summary>
    public void EndBlock()
    {
        if (palette.Enabled)
        {
            EndLine();
        }

        if (!_atLineStart)
        {
            output.Write('\n');
            _atLineStart = true;
        }

        _decidingPrefix = true;
    }

    private void Process(char character)
    {
        if (character == '\r')
        {
            return;
        }

        if (character == '\n')
        {
            EndLine();
            output.Write('\n');
            _atLineStart = true;
            _decidingPrefix = true;
            return;
        }

        if (_decidingPrefix)
        {
            DecidePrefix(character);
            return;
        }

        ProcessInline(character);
    }

    /// <summary>Closes whatever the current line opened and settles any fence transition.</summary>
    private void EndLine()
    {
        FlushPrefixLiterally();
        FlushHoldLiterally();
        if (_headingOpen)
        {
            _headingOpen = false;
            WriteRaw(palette.WeightClose);
        }

        if (_fenceLineOpen)
        {
            _fenceLineOpen = false;
            WriteRaw(palette.WeightClose);
        }

        if (_fenceToggled)
        {
            _fenceToggled = false;
            _inFence = !_inFence;
        }

        _literalRest = false;
    }

    /// <summary>
    /// Buffers the start of a line until it is known to be a fence, a heading, a bullet, or
    /// ordinary prose. Inside a fenced block only the closing fence is looked for.
    /// </summary>
    private void DecidePrefix(char character)
    {
        if (_prefix.Length >= MaxPrefixLength)
        {
            FlushPrefixLiterally();
            ProcessInline(character);
            return;
        }

        if (character == '`')
        {
            _prefix.Append(character);
            if (_prefix.ToString().TrimStart(' ') == Fence)
            {
                BeginFenceLine();
            }

            return;
        }

        if (_inFence)
        {
            FlushPrefixLiterally();
            ProcessInline(character);
            return;
        }

        var candidate = _prefix.ToString();
        if (character == ' ')
        {
            if (candidate.Length is > 0 and <= MaxHeadingLevel && candidate.All(static c => c == '#'))
            {
                _decidingPrefix = false;
                _prefix.Clear();
                WriteRaw(palette.BoldOpen);
                _headingOpen = true;
                return;
            }

            if (candidate.Length > 0 && IsBulletMarker(candidate))
            {
                _decidingPrefix = false;
                _prefix.Clear();
                WriteRaw(candidate[..^1]);
                WriteRaw(unicode ? ConsoleText.Bullet : ConsoleText.AsciiBullet);
                WriteRaw(" ");
                return;
            }

            if (candidate.Length == 0 || candidate.All(static c => c == ' '))
            {
                _prefix.Append(character);
                return;
            }

            FlushPrefixLiterally();
            ProcessInline(character);
            return;
        }

        if (character is '#' or '-' or '*' or '+')
        {
            _prefix.Append(character);
            return;
        }

        FlushPrefixLiterally();
        ProcessInline(character);
    }

    /// <summary>
    /// Renders a fence marker dimmed and hands the rest of the line — the language label — through
    /// literally. The block itself flips at the end of this line, so the marker still renders.
    /// </summary>
    private void BeginFenceLine()
    {
        var indent = _prefix.ToString();
        _prefix.Clear();
        _decidingPrefix = false;
        WriteRaw(indent[..^Fence.Length]);
        WriteRaw(palette.DimOpen);
        WriteRaw(Fence);
        _fenceLineOpen = true;
        _fenceToggled = true;
        _literalRest = true;
    }

    private static bool IsBulletMarker(string candidate) =>
        candidate[^1] is '-' or '*' or '+' && candidate[..^1].All(static c => c == ' ');

    /// <summary>Replays a buffered line prefix as ordinary text once it proved to be neither.</summary>
    private void FlushPrefixLiterally()
    {
        if (!_decidingPrefix)
        {
            return;
        }

        _decidingPrefix = false;
        if (_prefix.Length == 0)
        {
            return;
        }

        var buffered = _prefix.ToString();
        _prefix.Clear();
        foreach (var character in buffered)
        {
            ProcessInline(character);
        }
    }

    private void ProcessInline(char character)
    {
        if (_inFence || _literalRest)
        {
            WriteRaw(character.ToString());
            return;
        }

        switch (_hold)
        {
            case Hold.None:
                switch (character)
                {
                    case '`':
                        _hold = Hold.Code;
                        return;
                    case '*':
                        _hold = Hold.MaybeBold;
                        return;
                    default:
                        WriteRaw(character.ToString());
                        return;
                }

            case Hold.MaybeBold:
                if (character == '*')
                {
                    _hold = Hold.Bold;
                    return;
                }

                _hold = Hold.None;
                WriteRaw("*");
                ProcessInline(character);
                return;

            case Hold.Bold:
                if (character == '*')
                {
                    _hold = Hold.MaybeBoldClose;
                    return;
                }

                _held.Append(character);
                return;

            case Hold.MaybeBoldClose:
                if (character == '*')
                {
                    WriteSpan(palette.Bold, TakeHeld());
                    return;
                }

                _held.Append('*');
                _hold = Hold.Bold;
                ProcessInline(character);
                return;

            case Hold.Code:
                if (character == '`')
                {
                    var text = TakeHeld();

                    // An empty span is two adjacent backticks, not a code span: give them back.
                    if (text.Length == 0)
                    {
                        WriteRaw("``");
                        return;
                    }

                    WriteSpan(palette.Dim, text);
                    return;
                }

                _held.Append(character);
                return;

            default:
                return;
        }
    }

    private string TakeHeld()
    {
        var text = _held.ToString();
        _held.Clear();
        _hold = Hold.None;
        return text;
    }

    /// <summary>
    /// Writes a styled span, reopening an enclosing heading afterwards: bold and dim share SGR 22,
    /// so closing a span inside a heading would otherwise cancel the heading's own weight.
    /// </summary>
    private void WriteSpan(Func<string, string> style, string text)
    {
        WriteRaw(style(text));
        if (_headingOpen)
        {
            WriteRaw(palette.BoldOpen);
        }
    }

    /// <summary>Writes an unresolved marker and everything after it as the model sent it.</summary>
    private void FlushHoldLiterally()
    {
        var literal = _hold switch
        {
            Hold.MaybeBold => "*",
            Hold.Bold => "**" + _held,
            Hold.MaybeBoldClose => "**" + _held + "*",
            Hold.Code => "`" + _held,
            _ => string.Empty
        };

        _held.Clear();
        _hold = Hold.None;
        WriteRaw(literal);
    }

    private void WriteRaw(string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        output.Write(text);
        _atLineStart = false;
    }
}
