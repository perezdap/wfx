using System.Text.Json;

namespace Wfx.Mcp;

/// <summary>
/// Newline-delimited JSON-RPC 2.0 framing over a text stream pair. Requests carry integer
/// ids matched against responses. Frames that cannot be attributed (notifications,
/// unsupported server-to-client requests) are ignored, but a frame that violates the
/// framing contract faults the whole session: a server speaking garbage must fail its
/// tools with a structured error, never hang them.
/// </summary>
internal sealed class McpJsonRpcSession
{
    internal const int MaxLineCharacters = 10 * 1024 * 1024;

    private readonly TextWriter _output;
    private readonly TextReader _input;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _stateLock = new();
    private readonly Dictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private string? _fault;
    private long _nextId;

    public McpJsonRpcSession(TextWriter output, TextReader input)
    {
        _output = output;
        _input = input;
    }

    public string? FaultMessage
    {
        get
        {
            lock (_stateLock)
            {
                return _fault;
            }
        }
    }

    public void StartReadLoop() => _ = Task.Run(ReadLoopAsync);

    public async Task<JsonElement> RequestAsync(
        string method,
        Action<Utf8JsonWriter>? writeParameters,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_stateLock)
        {
            if (_fault is not null)
            {
                throw new McpConnectionException(_fault);
            }
        }

        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_stateLock)
        {
            _pending.Add(id, completion);
        }

        try
        {
            await WriteLineAsync(McpJsonRpc.BuildRequestLine(id, method, writeParameters), cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            lock (_stateLock)
            {
                _pending.Remove(id);
            }

            throw;
        }

        using var registration = cancellationToken.Register(static state =>
        {
            var (session, requestId, token) = ((McpJsonRpcSession Session, long Id, CancellationToken Token))state!;
            session.CancelPending(requestId, token);
        }, (this, id, cancellationToken));

        return await completion.Task.ConfigureAwait(false);
    }

    public async Task NotifyAsync(
        string method,
        Action<Utf8JsonWriter>? writeParameters,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_stateLock)
        {
            if (_fault is not null)
            {
                throw new McpConnectionException(_fault);
            }
        }

        await WriteLineAsync(McpJsonRpc.BuildNotificationLine(method, writeParameters), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Marks the session dead and fails every in-flight request. Idempotent; the first
    /// fault message wins so callers report the original cause.
    /// </summary>
    public void Fault(string message)
    {
        List<TaskCompletionSource<JsonElement>> abandoned;
        lock (_stateLock)
        {
            if (_fault is not null)
            {
                return;
            }

            _fault = message;
            abandoned = [.. _pending.Values];
            _pending.Clear();
        }

        foreach (var completion in abandoned)
        {
            completion.TrySetException(new McpConnectionException(message));
        }
    }

    private void CancelPending(long id, CancellationToken token)
    {
        TaskCompletionSource<JsonElement>? completion;
        lock (_stateLock)
        {
            _pending.Remove(id, out completion);
        }

        completion?.TrySetCanceled(token);
    }

    private async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _output.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (true)
            {
                var line = await _input.ReadLineAsync(CancellationToken.None).ConfigureAwait(false);
                if (line is null)
                {
                    Fault("The MCP server exited.");
                    return;
                }

                if (line.Length == 0)
                {
                    continue;
                }

                if (line.Length > MaxLineCharacters)
                {
                    Fault($"The MCP server sent a message larger than {MaxLineCharacters} characters.");
                    return;
                }

                if (!Dispatch(line))
                {
                    return;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Fault($"The MCP server connection closed: {exception.Message}");
        }
    }

    /// <summary>Returns false when the session faulted and the read loop must stop.</summary>
    private bool Dispatch(string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            // The stdio transport is newline-delimited JSON-RPC; anything unparseable is a
            // protocol violation and must surface as a structured failure, not a silent skip.
            Fault("The MCP server sent a malformed response.");
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                Fault("The MCP server sent a malformed response.");
                return false;
            }

            if (!root.TryGetProperty("id", out var idElement))
            {
                // Notifications are legitimate; any other id-less object is not a JSON-RPC
                // message the client can act on, but only notifications are expected here.
                return root.TryGetProperty("method", out _);
            }

            if (root.TryGetProperty("method", out _))
            {
                // A server-to-client request (sampling, roots). Out of scope for the
                // milestone; it is well-formed JSON-RPC, so ignore rather than fault.
                return true;
            }

            long id;
            if (idElement.ValueKind == JsonValueKind.Number && idElement.TryGetInt64(out id))
            {
                // Integer id, the normal case.
            }
            else if (idElement.ValueKind == JsonValueKind.String && long.TryParse(idElement.GetString(), out id))
            {
                // Tolerate numeric string ids.
            }
            else
            {
                Fault("The MCP server sent a response with an invalid id.");
                return false;
            }

            TaskCompletionSource<JsonElement>? completion;
            lock (_stateLock)
            {
                _pending.Remove(id, out completion);
            }

            if (completion is null)
            {
                return true;
            }

            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                var code = error.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt64(out var value)
                    ? value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : "unknown";
                var message = error.TryGetProperty("message", out var messageElement) &&
                    messageElement.ValueKind == JsonValueKind.String
                    ? messageElement.GetString()
                    : "unknown error";
                completion.TrySetException(new McpConnectionException($"MCP server returned error {code}: {message}"));
                return true;
            }

            if (root.TryGetProperty("result", out var result))
            {
                completion.TrySetResult(result.Clone());
                return true;
            }

            completion.TrySetException(new McpConnectionException("The MCP server returned a response without a result or error."));
            return true;
        }
    }
}
