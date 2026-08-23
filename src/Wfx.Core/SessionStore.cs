using System.Globalization;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace Wfx.Core;

/// <summary>
/// Creates append-only JSONL session logs under a per-user sessions directory.
/// Line 1 is a <c>header</c>; every later line is one event.
/// </summary>
public sealed class SessionStore
{
    public const int SchemaVersion = 1;

    private readonly string _directory;
    private readonly TimeProvider _time;

    public SessionStore(string? sessionsDirectory = null, TimeProvider? timeProvider = null)
    {
        _directory = Path.GetFullPath(sessionsDirectory ?? DefaultDirectory());
        _time = timeProvider ?? TimeProvider.System;
    }

    public static string DefaultDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".wfx", "sessions");

    public SessionLog Create(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        EnsureDirectory();
        var createdAt = _time.GetUtcNow();
        var workspace = Path.GetFullPath(workspaceRoot);
        IOException? lastCollision = null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var id = SessionId.Create(createdAt);
            var path = Path.Combine(_directory, id + ".jsonl");
            FileStream? stream = null;
            try
            {
                stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                var log = new SessionLog(id, path, stream);
                log.WriteHeader(SchemaVersion, id, createdAt, workspace);
                return log;
            }
            catch (IOException exception) when (File.Exists(path) && stream is null)
            {
                lastCollision = exception;
            }
            catch
            {
                stream?.Dispose();
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }

                throw;
            }
        }

        throw new IOException($"Could not create a unique session file under '{_directory}'.", lastCollision);
    }

    private void EnsureDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            EnsureWindowsDirectory();
            return;
        }

        Directory.CreateDirectory(_directory);
    }

    [SupportedOSPlatform("windows")]
    private void EnsureWindowsDirectory()
    {
        var parent = Path.GetDirectoryName(_directory);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var security = CurrentUserOnlyDirectorySecurity();
        var info = new DirectoryInfo(_directory);
        if (info.Exists)
        {
            info.SetAccessControl(security);
            return;
        }

        info.Create(security);
    }

    [SupportedOSPlatform("windows")]
    private static DirectorySecurity CurrentUserOnlyDirectorySecurity()
    {
        var user = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Cannot determine the current Windows user for the sessions directory ACL.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            user,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        return security;
    }
}

internal static class SessionId
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

    public static string Create(DateTimeOffset timestamp)
    {
        var utc = timestamp.UtcDateTime;
        var stamp = utc.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);
        return $"{stamp}Z-{RandomNumberGenerator.GetString(Alphabet, 6)}";
    }
}

/// <summary>
/// An open append-only session file. Writes are flushed to disk after each event.
/// </summary>
public sealed class SessionLog : IDisposable
{
    private readonly FileStream _stream;
    private readonly object _gate = new();
    private bool _disposed;

    internal SessionLog(string id, string filePath, FileStream stream)
    {
        Id = id;
        FilePath = filePath;
        _stream = stream;
    }

    public string Id { get; }

    public string FilePath { get; }

    internal void WriteHeader(int schemaVersion, string sessionId, DateTimeOffset createdAt, string workspace) =>
        Write(writer =>
        {
            writer.WriteString("type", "header");
            writer.WriteNumber("schema_version", schemaVersion);
            writer.WriteString("session_id", sessionId);
            writer.WriteString(
                "created_at",
                createdAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture));
            writer.WriteString("workspace", workspace);
        });

    internal void WriteTurnStarted(EndpointIdentity endpoint) =>
        Write(writer =>
        {
            writer.WriteString("type", "turn_started");
            WriteOptionalString(writer, "profile", endpoint.Profile);

            writer.WriteString("provider", endpoint.Provider);
            writer.WriteString("protocol", endpoint.Protocol);
            writer.WriteString("model", endpoint.Model);
        });

    internal void WriteMessage(ModelMessage message) =>
        Write(writer =>
        {
            writer.WriteString("type", "message");
            writer.WriteString("role", RoleName(message.Role));
            WriteOptionalString(writer, "content", message.Content);

            if (message.ToolCalls is { Count: > 0 })
            {
                writer.WriteStartArray("tool_calls");
                foreach (var call in message.ToolCalls)
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", call.Id);
                    writer.WriteString("name", call.Name);
                    writer.WriteString("arguments", call.ArgumentsJson);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            if (message.ToolCallId is not null)
            {
                writer.WriteString("tool_call_id", message.ToolCallId);
            }

            if (message.Name is not null)
            {
                writer.WriteString("name", message.Name);
            }

            if (!string.IsNullOrEmpty(message.ProviderItemsJson))
            {
                writer.WritePropertyName("provider_items");
                using var items = JsonDocument.Parse(message.ProviderItemsJson);
                items.RootElement.WriteTo(writer);
            }
        });

    internal void WriteUsage(ModelUsage usage) =>
        Write(writer =>
        {
            writer.WriteString("type", "usage");
            WriteOptionalInt64(writer, "input_tokens", usage.InputTokens);
            WriteOptionalInt64(writer, "output_tokens", usage.OutputTokens);
        });

    internal void WriteInterrupted() =>
        Write(static writer => writer.WriteString("type", "interrupted"));

    internal void WriteError(Exception exception) =>
        Write(writer =>
        {
            writer.WriteString("type", "error");
            writer.WriteString("message", exception.Message);
        });

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stream.Flush(flushToDisk: true);
            _stream.Dispose();
        }
    }

    private void Write(Action<Utf8JsonWriter> writeProperties)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writeProperties(writer);
                writer.WriteEndObject();
            }

            buffer.WriteByte((byte)'\n');
            _stream.Write(buffer.GetBuffer(), 0, (int)buffer.Length);
            // Durability unit is one complete JSONL line. Flush to disk before returning
            // so a crash loses at most the last event. The observer CancellationToken is
            // not honoured here: cancelling mid-flush can leave a truncated line, which
            // is a worse record than a finished one.
            _stream.Flush(flushToDisk: true);
        }
    }

    private static void WriteOptionalString(Utf8JsonWriter writer, string name, string? value)
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

    private static void WriteOptionalInt64(Utf8JsonWriter writer, string name, long? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteNumber(name, value.Value);
        }
    }

    private static string RoleName(ModelRole role) => role switch
    {
        ModelRole.System => "system",
        ModelRole.User => "user",
        ModelRole.Assistant => "assistant",
        ModelRole.Tool => "tool",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown message role.")
    };
}
