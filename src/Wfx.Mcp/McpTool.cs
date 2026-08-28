using System.Text.Json;
using System.Text.Json.Nodes;
using Wfx.Core;

namespace Wfx.Mcp;

/// <summary>
/// Adapts one MCP server tool to <see cref="ITool"/>. The server's JSON Schema passes
/// through as the tool's parameters, and the call is classified <see cref="ApprovalLevel.SystemChange"/>
/// unconditionally: third-party code runs with the server process's full authority, so an
/// MCP tool is never auto-approved as read-only. Per-call approval flows through the
/// ordinary approval service before <see cref="ExecuteAsync"/> sends anything.
/// </summary>
public sealed class McpTool : ITool
{
    private readonly string _serverName;
    private readonly McpToolInfo _info;
    private readonly McpStdioClient _client;

    public McpTool(string serverName, McpToolInfo info, McpStdioClient client, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _serverName = serverName;
        _info = info;
        _client = client;
        Definition = new ToolDefinition(name, BuildDescription(serverName, info), BuildParameters(info));
    }

    public ToolDefinition Definition { get; }

    public ApprovalLevel Classify(JsonElement arguments) => ApprovalLevel.SystemChange;

    public async ValueTask<ToolResult> ExecuteAsync(
        JsonElement arguments,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _client.CallToolAsync(_info.Name, arguments, cancellationToken).ConfigureAwait(false);
            return result.IsError
                ? ToolResult.Fail(string.IsNullOrWhiteSpace(result.Output)
                    ? $"MCP server '{_serverName}' reported an error."
                    : result.Output)
                : ToolResult.Ok(result.Output);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation propagates to the process: the server is killed and its tools
            // fail structurally for the rest of the run instead of hanging on a dead call.
            await _client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch (McpConnectionException exception)
        {
            return ToolResult.Fail($"MCP server '{_serverName}' failed: {exception.Message}");
        }
    }

    private static string BuildDescription(string serverName, McpToolInfo info) =>
        string.IsNullOrWhiteSpace(info.Description)
            ? $"MCP tool '{info.Name}' from server '{serverName}'."
            : info.Description!;

    private static JsonObject BuildParameters(McpToolInfo info) =>
        info.InputSchema is JsonObject schema
            ? (JsonObject)schema.DeepClone()
            : new JsonObject { ["type"] = "object" };
}
