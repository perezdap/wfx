using System.Text;

namespace Wfx.Mcp;

/// <summary>
/// Builds model-facing names for MCP tools: <c>mcp_&lt;server&gt;_&lt;tool&gt;</c>, sanitized to the
/// portable tool-name character set and capped so provider schemas stay valid. The
/// deterministic mapping means two different server tools can collide after sanitizing;
/// the host resolves collisions by keeping the first and warning about the rest.
/// </summary>
internal static class McpToolNames
{
    private const int MaxLength = 64;

    public static string ForTool(string serverName, string toolName)
    {
        var name = $"mcp_{Sanitize(serverName)}_{Sanitize(toolName)}";
        return name.Length <= MaxLength ? name : name[..MaxLength];
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unnamed";
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(char.IsAsciiLetterOrDigit(character) || character == '_' ? character : '_');
        }

        return builder.ToString();
    }
}
