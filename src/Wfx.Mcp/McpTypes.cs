using System.Text.Json;

namespace Wfx.Mcp;

/// <summary>
/// A structured MCP failure: the server could not start, exited, spoke an invalid protocol,
/// or returned a malformed or error response. Callers map these to structured tool failures;
/// an MCP failure never aborts the CLI or the turn.
/// </summary>
internal sealed class McpConnectionException : InvalidOperationException
{
    public McpConnectionException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// One tool listed by an MCP server's <c>tools/list</c> response. The input schema stays a
/// <see cref="JsonElement"/> clone until the tool adapter converts it to the
/// <c>ToolDefinition</c> node shape the registry contract requires.
/// </summary>
internal sealed record McpToolInfo(string Name, string? Description, JsonElement? InputSchema);

/// <summary>The mapped outcome of one <c>tools/call</c> round trip.</summary>
internal sealed record McpToolCallResult(bool IsError, string Output);
