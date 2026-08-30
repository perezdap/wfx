using System.Text;
using System.Text.Json;
using Wfx.Mcp;

namespace Wfx.Cli;

/// <summary>
/// Surfaces MCP sign-in reminders on stderr. These are not ordinary warnings: they are the
/// remediation the noninteractive contract promises, so they are never suppressed by
/// --quiet. Under --json each reminder is one structured JSON object per line (stdout stays
/// the turn's event stream); otherwise it is the plain warning text.
/// </summary>
internal static class McpAuthorizationReminderWriter
{
    public static void Report(IReadOnlyList<McpAuthorizationReminder> reminders, bool json)
    {
        foreach (var reminder in reminders)
        {
            if (json)
            {
                WriteJson(reminder);
            }
            else
            {
                Console.Error.WriteLine($"wfx: warning: {reminder.Message}");
            }
        }
    }

    private static void WriteJson(McpAuthorizationReminder reminder)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("event", "warning");
            writer.WriteString("kind", "mcp_authorization_required");
            writer.WriteString("server", reminder.ServerName);
            writer.WriteString("message", reminder.Message);
            writer.WriteString("remediation", reminder.Command);
            writer.WriteEndObject();
        }

        Console.Error.WriteLine(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
    }
}
