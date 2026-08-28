namespace Wfx.Mcp;

/// <summary>
/// A structured MCP failure: the server could not start, exited, spoke an invalid protocol,
/// or returned a JSON-RPC error. Callers map these to structured tool failures; an MCP
/// failure never aborts the CLI or the turn.
/// </summary>
public sealed class McpConnectionException : InvalidOperationException
{
    public McpConnectionException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>One tool listed by an MCP server's <c>tools/list</c> response.</summary>
public sealed record McpToolInfo(string Name, string? Description, System.Text.Json.Nodes.JsonNode? InputSchema);

/// <summary>The mapped outcome of one <c>tools/call</c> round trip.</summary>
public sealed record McpToolCallResult(bool IsError, string Output);
