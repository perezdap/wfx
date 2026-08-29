using System.Text;
using System.Text.Json;

namespace Wfx.Mcp;

/// <summary>
/// Builds newline-delimited JSON-RPC 2.0 frames with <see cref="Utf8JsonWriter"/> only; no
/// reflection-based or node-tree serialization anywhere in the transport.
/// </summary>
internal static class McpJsonRpc
{
    public static string BuildRequestLine(long id, string method, Action<Utf8JsonWriter>? writeParameters)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WriteNumber("id", id);
            writer.WriteString("method", method);
            WriteParameters(writer, writeParameters);
            writer.WriteEndObject();
        }

        return Decode(buffer);
    }

    public static string BuildNotificationLine(string method, Action<Utf8JsonWriter>? writeParameters)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WriteString("method", method);
            WriteParameters(writer, writeParameters);
            writer.WriteEndObject();
        }

        return Decode(buffer);
    }

    private static void WriteParameters(Utf8JsonWriter writer, Action<Utf8JsonWriter>? writeParameters)
    {
        if (writeParameters is null)
        {
            return;
        }

        writer.WritePropertyName("params");
        writeParameters(writer);
    }

    private static string Decode(MemoryStream buffer) =>
        Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
}
