using System.Globalization;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace Wfx.Core;

internal static class SessionMessageRoles
{
    private static readonly IReadOnlyDictionary<ModelRole, string> Names = new Dictionary<ModelRole, string>
    {
        [ModelRole.System] = "system",
        [ModelRole.User] = "user",
        [ModelRole.Assistant] = "assistant",
        [ModelRole.Tool] = "tool"
    };

    public static string Name(ModelRole role) =>
        Names.TryGetValue(role, out var name)
            ? name
            : throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown message role.");

    public static bool TryParse(string name, out ModelRole role)
    {
        foreach (var pair in Names)
        {
            if (string.Equals(pair.Value, name, StringComparison.Ordinal))
            {
                role = pair.Key;
                return true;
            }
        }

        role = default;
        return false;
    }
}

public interface ISessionStore
{
    SessionLog Create(string workspaceRoot);

    SessionLog Open(string sessionId);

    SessionTranscript Read(string sessionId);

    IReadOnlyList<SessionSummary> List();

    long TotalSizeBytes();

    SessionSummary? FindLatest(string workspaceRoot);
}

public sealed record SessionTranscript(
    string SessionId,
    string FilePath,
    string Workspace,
    DateTimeOffset CreatedAt,
    IReadOnlyList<ModelMessage> Messages,
    EndpointIdentity? LastEndpoint);

/// <summary>
/// Creates append-only JSONL session logs under a per-user sessions directory.
/// Line 1 is a <c>header</c>; every later line is one event.
/// </summary>
public sealed class SessionStore : ISessionStore
{
    private const int TailScanChunkSize = 64 * 1024;

    internal const string InterruptedMarker =
        "The previous turn was cancelled before all tool results were recorded. Do not assume its unfinished tool calls completed.";
    internal const byte NeverReboundLeaseMarker = (byte)'N';
    internal const byte ReboundLeaseMarker = (byte)'R';

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

    public SessionLog Open(string sessionId)
    {
        var path = ExistingSessionPath(sessionId);
        var sessionLease = AcquireSessionLease(sessionId);
        FileStream? stream = null;

        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            TruncateIncompleteTail(stream);
            return new SessionLog(sessionId, path, stream, sessionLease);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            stream?.Dispose();
            sessionLease.Dispose();
            throw new IOException($"Could not reopen session '{sessionId}' for append: {exception.Message}", exception);
        }
    }

    public SessionTranscript Read(string sessionId)
    {
        var path = ExistingSessionPath(sessionId);

        var lines = ReadCompleteLines(path);
        if (lines.Count == 0)
        {
            throw new InvalidDataException($"Session '{sessionId}' is empty; a header is required.");
        }

        JsonDocument headerDocument;
        try
        {
            headerDocument = JsonDocument.Parse(lines[0]);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Session '{sessionId}' has an invalid header: {exception.Message}", exception);
        }

        using (headerDocument)
        {
            var header = headerDocument.RootElement;
            if (header.ValueKind != JsonValueKind.Object ||
                !header.TryGetProperty("type", out var type) ||
                type.GetString() != "header")
            {
                throw new InvalidDataException($"Session '{sessionId}' must begin with a header event.");
            }

            if (!header.TryGetProperty("schema_version", out var schemaVersionProperty) ||
                !schemaVersionProperty.TryGetInt32(out var schemaVersion))
            {
                throw new InvalidDataException($"Session '{sessionId}' header has no valid schema_version.");
            }

            if (schemaVersion > SchemaVersion)
            {
                throw new InvalidDataException(
                    $"Session '{sessionId}' uses schema_version {schemaVersion}, but this build supports version {SchemaVersion}. Upgrade WFX to resume it.");
            }

            var workspace = RequiredString(header, "workspace", sessionId, "header");
            var createdAtText = RequiredString(header, "created_at", sessionId, "header");
            if (!DateTimeOffset.TryParse(
                    createdAtText,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out var createdAt))
            {
                throw new InvalidDataException($"Session '{sessionId}' header has an invalid created_at value.");
            }

            var messages = new List<ModelMessage>();
            EndpointIdentity? lastEndpoint = null;
            for (var index = 1; index < lines.Count; index++)
            {
                if (string.IsNullOrWhiteSpace(lines[index]))
                {
                    continue;
                }

                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(lines[index]);
                }
                catch (JsonException exception)
                {
                    throw new InvalidDataException(
                        $"Session '{sessionId}' has malformed JSON on line {index + 1}: {exception.Message}",
                        exception);
                }

                using (document)
                {
                    var eventRoot = document.RootElement;
                    if (eventRoot.ValueKind != JsonValueKind.Object ||
                        !eventRoot.TryGetProperty("type", out var eventType) ||
                        eventType.ValueKind != JsonValueKind.String)
                    {
                        throw new InvalidDataException(
                            $"Session '{sessionId}' has an event without a valid type on line {index + 1}.");
                    }

                    switch (eventType.GetString())
                    {
                        case "turn_started":
                            lastEndpoint = ReadEndpoint(eventRoot, sessionId, index + 1);
                            break;
                        case "message":
                            messages.Add(ReadMessage(eventRoot, sessionId, index + 1));
                            break;
                        case "workspace_rebound":
                            workspace = RequiredString(
                                eventRoot,
                                "workspace",
                                sessionId,
                                "workspace_rebound",
                                index + 1);
                            break;
                        case "interrupted":
                            RepairInterruptedTail(messages);
                            break;
                        case "header":
                            throw new InvalidDataException(
                                $"Session '{sessionId}' contains a second header on line {index + 1}.");
                        default:
                            // Version 1 vocabulary: turn_started, message, usage, interrupted, error.
                            // Unknown event types are ignored for forward-compatible reads.
                            break;
                    }
                }
            }

            RepairInterruptedTail(messages);
            return new SessionTranscript(
                sessionId,
                path,
                workspace,
                createdAt,
                messages,
                lastEndpoint);
        }
    }

    public SessionSummary? FindLatest(string workspaceRoot)
    {
        var workspace = Path.GetFullPath(workspaceRoot);
        return List().FirstOrDefault(summary =>
            summary.Workspace is not null && SessionWorkspace.IsSame(summary.Workspace, workspace));
    }

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
            FileStream? sessionLease = null;
            FileStream? stream = null;
            try
            {
                sessionLease = OpenSessionLease(id);
                stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                InitializeNewSessionLease(sessionLease);
                var log = new SessionLog(id, path, stream, sessionLease);
                log.WriteHeader(SchemaVersion, id, createdAt, workspace);
                return log;
            }
            catch (IOException exception) when (File.Exists(path) && stream is null)
            {
                sessionLease?.Dispose();
                lastCollision = exception;
            }
            catch
            {
                stream?.Dispose();
                sessionLease?.Dispose();
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

    /// <summary>
    /// Lock-free listing of every session under the store root. Reads no locks, so it succeeds
    /// while another process is appending to a session. Malformed or unreadable files are skipped.
    /// </summary>
    public IReadOnlyList<SessionSummary> List()
    {
        if (!Directory.Exists(_directory))
        {
            return [];
        }

        var summaries = new List<SessionSummary>();
        foreach (var file in Directory.EnumerateFiles(_directory, "*.jsonl"))
        {
            var summary = ReadSummary(file);
            if (summary is not null)
            {
                summaries.Add(summary);
            }
        }

        summaries.Sort(static (left, right) => right.UpdatedAt.CompareTo(left.UpdatedAt));
        return summaries;
    }

    /// <summary>Total logical bytes consumed by session logs and their lease sidecars.</summary>
    public long TotalSizeBytes()
    {
        if (!Directory.Exists(_directory))
        {
            return 0;
        }

        long total = 0;
        foreach (var pattern in new[] { "*.jsonl", "*.lock" })
        {
            foreach (var path in Directory.EnumerateFiles(_directory, pattern))
            {
                try
                {
                    total += new FileInfo(path).Length;
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        return total;
    }

    private string SessionPath(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (sessionId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !string.Equals(Path.GetFileName(sessionId), sessionId, StringComparison.Ordinal))
        {
            throw new FileNotFoundException($"No session with ID '{sessionId}'.");
        }

        return Path.Combine(_directory, sessionId + ".jsonl");
    }

    private string ExistingSessionPath(string sessionId)
    {
        var path = SessionPath(sessionId);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"No session with ID '{sessionId}'.", path);
        }

        return path;
    }

    private FileStream AcquireSessionLease(string sessionId)
    {
        try
        {
            return OpenSessionLease(sessionId);
        }
        catch (IOException exception)
        {
            throw new IOException($"Session in use: '{sessionId}'.", exception);
        }
    }

    private FileStream OpenSessionLease(string sessionId) =>
        new(
            Path.Combine(_directory, sessionId + ".lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.Read);

    private static void InitializeNewSessionLease(FileStream lease)
    {
        lease.Position = 0;
        lease.WriteByte(NeverReboundLeaseMarker);
        lease.SetLength(1);
        lease.Flush(flushToDisk: true);
    }

    private static IReadOnlyList<string> ReadCompleteLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();
        if (text.Length == 0)
        {
            return [];
        }

        var lines = text.Split('\n').ToList();
        lines.RemoveAt(lines.Count - 1);

        return lines
            .Select(static line => line.EndsWith('\r') ? line[..^1] : line)
            .ToArray();
    }

    private static void TruncateIncompleteTail(FileStream stream)
    {
        var length = stream.Length;
        if (length == 0)
        {
            throw new IOException("Session file is empty and has no complete newline-terminated event.");
        }

        stream.Position = length - 1;
        if (stream.ReadByte() == '\n')
        {
            stream.Position = length;
            return;
        }

        var buffer = new byte[(int)Math.Min(TailScanChunkSize, length)];
        var end = length;
        while (end > 0)
        {
            var start = Math.Max(0, end - buffer.Length);
            var count = (int)(end - start);
            stream.Position = start;
            var offset = 0;
            while (offset < count)
            {
                var read = stream.Read(buffer, offset, count - offset);
                if (read == 0)
                {
                    throw new IOException("Session file could not be scanned for a complete event.");
                }

                offset += read;
            }

            for (var index = count - 1; index >= 0; index--)
            {
                if (buffer[index] == '\n')
                {
                    var truncateAt = start + index + 1;
                    stream.SetLength(truncateAt);
                    stream.Position = truncateAt;
                    return;
                }
            }

            end = start;
        }

        throw new IOException("Session file has no complete newline-terminated event.");
    }

    private static EndpointIdentity ReadEndpoint(JsonElement root, string sessionId, int line)
    {
        return new EndpointIdentity(
            OptionalString(root, "profile", sessionId, line),
            RequiredString(root, "provider", sessionId, "turn_started", line),
            RequiredString(root, "protocol", sessionId, "turn_started", line),
            RequiredString(root, "model", sessionId, "turn_started", line));
    }

    private static ModelMessage ReadMessage(JsonElement root, string sessionId, int line)
    {
        var roleName = RequiredString(root, "role", sessionId, "message", line);
        if (!SessionMessageRoles.TryParse(roleName, out var role))
        {
            throw new InvalidDataException(
                $"Session '{sessionId}' has unknown message role '{roleName}' on line {line}.");
        }

        List<ModelToolCall>? toolCalls = null;
        if (root.TryGetProperty("tool_calls", out var toolCallsElement))
        {
            if (toolCallsElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException($"Session '{sessionId}' has invalid tool_calls on line {line}.");
            }

            toolCalls = [];
            foreach (var call in toolCallsElement.EnumerateArray())
            {
                toolCalls.Add(new ModelToolCall(
                    RequiredString(call, "id", sessionId, "tool call", line),
                    RequiredString(call, "name", sessionId, "tool call", line),
                    RequiredString(call, "arguments", sessionId, "tool call", line)));
            }
        }

        string? providerItems = null;
        if (root.TryGetProperty("provider_items", out var providerItemsElement))
        {
            providerItems = providerItemsElement.GetRawText();
        }

        return new ModelMessage(
            role,
            OptionalString(root, "content", sessionId, line),
            toolCalls,
            OptionalString(root, "tool_call_id", sessionId, line),
            OptionalString(root, "name", sessionId, line),
            providerItems);
    }

    private static void RepairInterruptedTail(List<ModelMessage> messages)
    {
        var assistantIndex = messages.Count - 1;
        while (assistantIndex >= 0 && messages[assistantIndex].Role == ModelRole.Tool)
        {
            assistantIndex--;
        }

        if (assistantIndex < 0 ||
            messages[assistantIndex].Role != ModelRole.Assistant ||
            messages[assistantIndex].ToolCalls is not { Count: > 0 } calls)
        {
            return;
        }

        var results = messages.Skip(assistantIndex + 1).ToArray();
        var callIds = calls.Select(static call => call.Id).ToHashSet(StringComparer.Ordinal);
        var resultIds = results
            .Where(static result => result.ToolCallId is not null)
            .Select(static result => result.ToolCallId!)
            .ToHashSet(StringComparer.Ordinal);
        var complete = callIds.Count == calls.Count &&
            resultIds.Count == results.Length &&
            callIds.SetEquals(resultIds);
        if (complete)
        {
            return;
        }

        messages.RemoveRange(assistantIndex, messages.Count - assistantIndex);
        messages.Add(new ModelMessage(ModelRole.System, InterruptedMarker));
    }

    private static string RequiredString(JsonElement root, string name, string sessionId, string eventName, int? line = null)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrEmpty(value.GetString()))
        {
            var location = line is null ? "header" : $"line {line}";
            throw new InvalidDataException($"Session '{sessionId}' has an invalid {name} in {eventName} on {location}.");
        }

        return value.GetString()!;
    }

    private static string? OptionalString(JsonElement root, string name, string sessionId, int line) =>
        root.ValueKind != JsonValueKind.Object ||
        !root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null
            ? null
            : value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : throw new InvalidDataException(
                    $"Session '{sessionId}' event property '{name}' must be a string or null on line {line}.");

    private static SessionSummary? ReadSummary(string path)
    {
        // The lease sidecar doubles as a rebind index. New never-rebound sessions carry an
        // explicit marker and need only their header; old/copied/rebound sessions are scanned.
        try
        {
            using var reader = new StreamReader(
                new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            var headerLine = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(headerLine))
            {
                return null;
            }

            using var document = JsonDocument.Parse(headerLine);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "header")
            {
                return null;
            }

            var info = new FileInfo(path);
            var sessionId = root.TryGetProperty("session_id", out var sessionIdProperty)
                ? sessionIdProperty.GetString()
                : null;
            var workspace = root.TryGetProperty("workspace", out var workspaceProperty)
                ? workspaceProperty.GetString()
                : null;
            var createdAt = root.TryGetProperty("created_at", out var createdProperty)
                && DateTimeOffset.TryParse(
                    createdProperty.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out var createdOffset)
                    ? (DateTime?)createdOffset.UtcDateTime
                    : null;

            if (MustScanWorkspaceRebounds(path))
            {
                var lines = ReadCompleteLines(path);
                for (var index = 1; index < lines.Count; index++)
                {
                    try
                    {
                        using var eventDocument = JsonDocument.Parse(lines[index]);
                        var eventRoot = eventDocument.RootElement;
                        if (eventRoot.TryGetProperty("type", out var eventType) &&
                            eventType.GetString() == "workspace_rebound" &&
                            eventRoot.TryGetProperty("workspace", out var reboundWorkspace) &&
                            reboundWorkspace.ValueKind == JsonValueKind.String)
                        {
                            workspace = reboundWorkspace.GetString();
                        }
                    }
                    catch (JsonException)
                    {
                        // Listing remains available for a session whose later history is malformed.
                    }
                }
            }

            return new SessionSummary(
                sessionId ?? Path.GetFileNameWithoutExtension(path),
                workspace,
                createdAt,
                info.LastWriteTimeUtc,
                info.Length);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool MustScanWorkspaceRebounds(string sessionPath)
    {
        var leasePath = Path.ChangeExtension(sessionPath, ".lock");
        try
        {
            using var lease = new FileStream(
                leasePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);
            return lease.Length != 1 || lease.ReadByte() != NeverReboundLeaseMarker;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
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
    private readonly FileStream _sessionLease;
    private readonly object _gate = new();
    private bool _disposed;

    internal SessionLog(string id, string filePath, FileStream stream, FileStream sessionLease)
    {
        Id = id;
        FilePath = filePath;
        _stream = stream;
        _sessionLease = sessionLease;
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

    internal void WriteWorkspaceRebound(string workspace)
    {
        MarkWorkspaceRebound();
        Write(writer =>
        {
            writer.WriteString("type", "workspace_rebound");
            writer.WriteString("workspace", workspace);
        });
    }

    private void MarkWorkspaceRebound()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _sessionLease.Position = 0;
            _sessionLease.WriteByte(SessionStore.ReboundLeaseMarker);
            _sessionLease.SetLength(1);
            _sessionLease.Flush(flushToDisk: true);
        }
    }

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
                writer.WriteRawValue(message.ProviderItemsJson);
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
            try
            {
                _stream.Flush(flushToDisk: true);
            }
            finally
            {
                try
                {
                    _stream.Dispose();
                }
                finally
                {
                    _sessionLease.Dispose();
                }
            }
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

    private static string RoleName(ModelRole role) => SessionMessageRoles.Name(role);
}

/// <summary>
/// One line of a session file, enough to list a session without loading its transcript.
/// </summary>
public sealed record SessionSummary(
    string SessionId,
    string? Workspace,
    DateTime? CreatedAt,
    DateTime UpdatedAt,
    long SizeBytes);
