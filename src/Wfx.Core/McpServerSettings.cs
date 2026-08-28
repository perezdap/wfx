namespace Wfx.Core;

/// <summary>
/// One user-configured MCP stdio server: the command to launch, its arguments, and extra
/// environment variables. MCP servers are read from the user configuration layer only; a
/// project configuration supplying <c>mcp_servers</c> is a configuration error.
/// </summary>
public sealed record McpServerSettings(
    string Command,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment);
