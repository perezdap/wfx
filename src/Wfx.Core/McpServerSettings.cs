namespace Wfx.Core;

/// <summary>
/// One user-configured MCP server: either a stdio server (the command to launch, its
/// arguments, and extra environment variables) or a remote Streamable HTTP server (the
/// endpoint URL and extra request headers). Exactly one transport is present; configuration
/// validation rejects an entry with neither or both. MCP servers are read from the user
/// configuration layer only; a project configuration supplying <c>mcp_servers</c> is a
/// configuration error.
/// </summary>
public sealed record McpServerSettings
{
    private static readonly IReadOnlyDictionary<string, string> NoHeaders =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private McpServerSettings(
        string? command,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        string? url,
        IReadOnlyDictionary<string, string> headers)
    {
        Command = command;
        Arguments = arguments;
        Environment = environment;
        Url = url;
        Headers = headers;
    }

    /// <summary>The stdio command to launch; <c>null</c> for HTTP servers.</summary>
    public string? Command { get; }

    /// <summary>Stdio command arguments; empty for HTTP servers.</summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>Extra environment variables for the stdio child process; empty for HTTP servers.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; }

    /// <summary>The Streamable HTTP endpoint URL; <c>null</c> for stdio servers.</summary>
    public string? Url { get; }

    /// <summary>
    /// Extra HTTP request headers. Values may carry credentials; they are secrets and are
    /// never logged or written to the event stream.
    /// </summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>True when this entry describes a remote HTTP server.</summary>
    public bool IsHttp => Url is not null;

    public static McpServerSettings ForStdio(
        string command,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment) =>
        new(command, arguments, environment, null, NoHeaders);

    public static McpServerSettings ForHttp(string url, IReadOnlyDictionary<string, string>? headers = null) =>
        new(null, [], new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), url, headers ?? NoHeaders);
}
