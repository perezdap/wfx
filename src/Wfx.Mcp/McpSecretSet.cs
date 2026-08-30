using System.Collections;

namespace Wfx.Mcp;

/// <summary>
/// The live set of MCP secrets that must never reach logs, approval prompts, or the event
/// stream: configured header values plus stored and refreshed OAuth tokens. Thread-safe;
/// enumeration snapshots so a token landing mid-redaction cannot corrupt an in-flight read.
/// </summary>
internal sealed class McpSecretSet : IReadOnlyList<string>
{
    private readonly object _gate = new();
    private readonly List<string> _secrets = [];

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _secrets.Count;
            }
        }
    }

    public string this[int index]
    {
        get
        {
            lock (_gate)
            {
                return _secrets[index];
            }
        }
    }

    public void Add(string? secret)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return;
        }

        lock (_gate)
        {
            if (!_secrets.Contains(secret))
            {
                _secrets.Add(secret);
            }
        }
    }

    public IEnumerator<string> GetEnumerator()
    {
        string[] snapshot;
        lock (_gate)
        {
            snapshot = [.. _secrets];
        }

        return ((IEnumerable<string>)snapshot).GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
