using System.Text.Json;

namespace Wfx.Mcp;

/// <summary>
/// A structured MCP failure: the server could not start, exited, spoke an invalid protocol,
/// or returned a malformed or error response. Callers map these to structured tool failures;
/// an MCP failure never aborts the CLI or the turn.
/// </summary>
internal class McpConnectionException : InvalidOperationException
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

/// <summary>
/// One live MCP server connection, regardless of transport (stdio child process or
/// Streamable HTTP endpoint). The tool adapter and the host are transport-agnostic; only
/// the byte-mover behind the connection differs.
/// </summary>
internal interface IMcpServerConnection : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default);

    Task<McpToolCallResult> CallToolAsync(
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// An MCP authorization failure: the HTTP endpoint rejected the request with 401/403 and no
/// valid credential is stored. The message carries the remediation (run
/// <c>wfx mcp auth &lt;server&gt;</c>); it never carries a token.
/// </summary>
internal sealed class McpAuthorizationException : McpConnectionException
{
    public McpAuthorizationException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
