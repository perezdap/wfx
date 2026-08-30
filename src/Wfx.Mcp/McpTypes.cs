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
/// An MCP authorization failure: the HTTP endpoint rejected the request with 401 and no valid
/// credential is stored. The message carries the remediation (run
/// <c>wfx mcp auth &lt;server&gt;</c>); it never carries a token. A 403 is not an auth
/// challenge — it surfaces as an ordinary <see cref="McpConnectionException"/>.
/// </summary>
internal sealed class McpAuthorizationException : McpConnectionException
{
    public McpAuthorizationException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// One server that refused the handshake with a 401 and holds no usable credential. The
/// remediation is part of the CLI's contract with the user: it must always be surfaced, even
/// when ordinary warnings are suppressed.
/// </summary>
public sealed record McpAuthorizationReminder(string ServerName, string Command, string Message);

/// <summary>How an explicit <c>wfx mcp auth</c> sign-in or revocation ended.</summary>
public enum McpAuthorizationOutcome
{
    SignedIn,
    CredentialRemoved,
    NoStoredCredential,
    ServerNotConfigured,
    ServerNotHttp,
    Failed
}

/// <summary>
/// The outcome of <see cref="McpHost.AuthorizeAsync"/> or <see cref="McpHost.Revoke"/>: what
/// happened, plus the user-facing message. The caller maps the outcome to its own
/// presentation and exit code. Messages never carry token material.
/// </summary>
public sealed record McpAuthorizationResult(McpAuthorizationOutcome Outcome, string Message);

/// <summary>The single composer of the sign-in remediation text, so transport and host never drift.</summary>
internal static class McpSignInRemediation
{
    public static string Command(string serverName) => $"wfx mcp auth {serverName}";

    public static string Message(string serverName) =>
        $"MCP server '{serverName}' requires authorization. Run '{Command(serverName)}' to sign in.";
}
