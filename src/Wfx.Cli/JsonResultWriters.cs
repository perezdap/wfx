using System.Globalization;
using System.Text.Json;
using Wfx.Core;

namespace Wfx.Cli;

/// <summary>
/// Typed emitters for the <c>--json</c> result objects of the non-turn commands
/// (<c>sessions</c>, <c>config</c>, <c>models</c>). Every shape is written through
/// <see cref="Utf8JsonWriter"/> directly so the output stays reflection-free and
/// Native AOT-clean, matching the contract published under docs/schemas/.
/// </summary>
internal static class JsonResultWriters
{
    public const int SchemaVersion = 1;

    public static void WriteSessionsResult(Utf8JsonWriter writer, SessionListing listing)
    {
        writer.WriteNumber("schema_version", SchemaVersion);
        writer.WriteStartArray("sessions");
        foreach (var session in listing.Sessions)
        {
            writer.WriteStartObject();
            writer.WriteString("id", session.SessionId);
            WriteStringOrNull(writer, "workspace", session.Workspace);
            WriteTimestampOrNull(writer, "created_at", session.CreatedAt);
            WriteTimestamp(writer, "updated_at", session.UpdatedAt);
            writer.WriteNumber("size_bytes", session.SizeBytes);
            if (session.LastEndpoint is { } endpoint)
            {
                writer.WritePropertyName("endpoint");
                WriteEndpoint(writer, endpoint);
            }
            else
            {
                writer.WriteNull("endpoint");
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    public static void WriteConfigResult(Utf8JsonWriter writer, WfxSettings settings)
    {
        writer.WriteNumber("schema_version", SchemaVersion);
        writer.WritePropertyName("effective");
        writer.WriteStartObject();
        writer.WriteString("provider", settings.Provider);
        writer.WriteString("protocol", settings.Protocol);
        writer.WriteString("base_url", settings.BaseUri.ToString());
        if (settings.ApiKey is not null)
        {
            writer.WriteString("api_key", "[REDACTED]");
        }

        writer.WriteString("model", settings.Model);
        if (settings.Headers.Count > 0)
        {
            writer.WriteStartObject("headers");
            foreach (var header in settings.Headers)
            {
                writer.WriteString(header.Key, "[REDACTED]");
            }

            writer.WriteEndObject();
        }

        writer.WriteNumber("timeout_seconds", (int)settings.Timeout.TotalSeconds);
        writer.WriteNumber("max_iterations", settings.MaxIterations);
        writer.WriteString("approval", WfxConfiguration.FormatApprovalMode(settings.Approval));
        WriteStringOrNull(writer, "profile", settings.Profile);
        writer.WriteEndObject();

        writer.WriteStartArray("sources");
        foreach (var source in settings.Sources)
        {
            writer.WriteStartObject();
            writer.WriteString("layer", source.Layer);
            WriteStringOrNull(writer, "path", source.Path);
            writer.WriteStartArray("keys");
            foreach (var key in source.Keys)
            {
                writer.WriteStringValue(key);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    public static void WriteModelsResult(Utf8JsonWriter writer, WfxSettings settings)
    {
        writer.WriteNumber("schema_version", SchemaVersion);
        writer.WriteStartArray("profiles");
        foreach (var profile in settings.ConfiguredModelProfiles)
        {
            writer.WriteStartObject();
            writer.WriteString("name", profile.Name);
            writer.WriteString("provider", profile.Provider);
            WriteStringOrNull(writer, "protocol", profile.Protocol);
            WriteStringOrNull(writer, "base_url", profile.BaseUri?.ToString());
            writer.WriteString("model", profile.Model);
            writer.WriteBoolean("has_credentials", profile.HasCredentials);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    internal static void WriteEndpoint(Utf8JsonWriter writer, EndpointIdentity endpoint)
    {
        writer.WriteStartObject();
        WriteStringOrNull(writer, "profile", endpoint.Profile);
        writer.WriteString("provider", endpoint.Provider);
        writer.WriteString("protocol", endpoint.Protocol);
        writer.WriteString("model", endpoint.Model);
        writer.WriteEndObject();
    }

    private static void WriteStringOrNull(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static void WriteTimestampOrNull(Utf8JsonWriter writer, string name, DateTime? value)
    {
        if (value is { } timestamp)
        {
            WriteTimestamp(writer, name, timestamp);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static void WriteTimestamp(Utf8JsonWriter writer, string name, DateTime value) =>
        writer.WriteString(
            name,
            value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture));
}
