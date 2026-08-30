using System.Net;
using System.Net.Sockets;
using System.Text;

// Shared across test assemblies via a linked Compile item (Wfx.Cli.Tests includes this file).
namespace Wfx.Testing;

/// <summary>One HTTP request observed by <see cref="LoopbackHttpServer"/>.</summary>
internal sealed record LoopbackRequest(
    string Method,
    string Path,
    string Query,
    IReadOnlyDictionary<string, string> Headers,
    string Body);

/// <summary>
/// The response a handler returns. When <see cref="CloseWithoutContentLength"/> is set the
/// body is close-delimited, simulating an SSE stream that ends when the server hangs up.
/// </summary>
internal sealed record LoopbackResponse(
    int Status,
    string ContentType,
    string Body,
    IReadOnlyDictionary<string, string>? ExtraHeaders = null,
    bool CloseWithoutContentLength = false)
{
    public static LoopbackResponse Json(string body, int status = 200) =>
        new(status, "application/json", body);

    public static LoopbackResponse Accepted() =>
        new(202, "text/plain", string.Empty);
}

/// <summary>
/// A minimal deterministic HTTP/1.1 server on a loopback TCP socket: one request per
/// connection, no TLS, no keep-alive. Exists because <see cref="HttpListener"/> requires
/// http.sys URL ACL reservations the test user may not have, and ASP.NET would drag a web
/// framework into a transport test.
/// </summary>
internal sealed class LoopbackHttpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<LoopbackRequest, CancellationToken, Task<LoopbackResponse>> _handler;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _acceptLoop;
    private readonly List<LoopbackRequest> _requests = [];
    private readonly object _requestsLock = new();

    /// <summary>The last handler/serve failure, for diagnosing test doubles; null when healthy.</summary>
    public Exception? LastError { get; private set; }

    public LoopbackHttpServer(Func<LoopbackRequest, CancellationToken, Task<LoopbackResponse>> handler)
    {
        _handler = handler;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public LoopbackHttpServer(Func<LoopbackRequest, LoopbackResponse> handler)
        : this((request, _) => Task.FromResult(handler(request)))
    {
    }

    public Uri BaseUri => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");

    public IReadOnlyList<LoopbackRequest> Requests
    {
        get
        {
            lock (_requestsLock)
            {
                return [.. _requests];
            }
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _listener.Stop();
        try
        {
            _acceptLoop.Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception exception) when (exception is AggregateException or ObjectDisposedException or SocketException)
        {
            // Teardown must not fail the test.
        }

        _shutdown.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_shutdown.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => ServeConnectionAsync(client));
        }
    }

    private async Task ServeConnectionAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var request = await ReadRequestAsync(client, _shutdown.Token).ConfigureAwait(false);
                if (request is null)
                {
                    return;
                }

                lock (_requestsLock)
                {
                    _requests.Add(request);
                }

                var response = await _handler(request, _shutdown.Token).ConfigureAwait(false);
                await WriteResponseAsync(client, response, _shutdown.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or SocketException
                or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
            {
                // The client hung up (cancellation, disposal); nothing to report in a test double.
            }
            catch (Exception exception)
            {
                LastError = exception;
            }
        }
    }

    private static async Task<LoopbackRequest?> ReadRequestAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var stream = client.GetStream();
        var headerLines = new List<string>();
        var line = new StringBuilder();
        var buffer = new byte[1];
        // Header lines are ASCII; read byte-by-byte to avoid over-reading into the body.
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return headerLines.Count == 0 ? null : throw new InvalidOperationException("Truncated HTTP request.");
            }

            var ch = (char)buffer[0];
            if (ch == '\r')
            {
                continue;
            }

            if (ch == '\n')
            {
                if (line.Length == 0)
                {
                    break;
                }

                headerLines.Add(line.ToString());
                line.Clear();
                continue;
            }

            line.Append(ch);
        }

        if (headerLines.Count == 0)
        {
            return null;
        }

        var requestLine = headerLines[0].Split(' ');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var headerLine in headerLines.Skip(1))
        {
            var colon = headerLine.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                headers[headerLine[..colon].Trim()] = headerLine[(colon + 1)..].Trim();
            }
        }

        var body = string.Empty;
        if (headers.TryGetValue("Content-Length", out var lengthText) && int.TryParse(lengthText, out var length) && length > 0)
        {
            var bodyBytes = new byte[length];
            var offset = 0;
            while (offset < length)
            {
                var read = await stream.ReadAsync(
                    bodyBytes.AsMemory(offset, length - offset), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new InvalidOperationException("Truncated HTTP request body.");
                }

                offset += read;
            }

            body = Encoding.UTF8.GetString(bodyBytes);
        }

        var target = requestLine[1];
        var question = target.IndexOf('?', StringComparison.Ordinal);
        return new LoopbackRequest(
            requestLine[0],
            question < 0 ? target : target[..question],
            question < 0 ? string.Empty : target[(question + 1)..],
            headers,
            body);
    }

    private static async Task WriteResponseAsync(
        TcpClient client,
        LoopbackResponse response,
        CancellationToken cancellationToken)
    {
        var stream = client.GetStream();
        var body = Encoding.UTF8.GetBytes(response.Body);
        var reason = response.Status switch
        {
            200 => "OK",
            202 => "Accepted",
            204 => "No Content",
            302 => "Found",
            400 => "Bad Request",
            401 => "Unauthorized",
            403 => "Forbidden",
            404 => "Not Found",
            405 => "Method Not Allowed",
            500 => "Internal Server Error",
            _ => "Status"
        };
        var builder = new StringBuilder();
        builder.Append("HTTP/1.1 ").Append(response.Status).Append(' ').Append(reason).Append("\r\n");
        builder.Append("Content-Type: ").Append(response.ContentType).Append("\r\n");
        if (response.ExtraHeaders is not null)
        {
            foreach (var (name, value) in response.ExtraHeaders)
            {
                builder.Append(name).Append(": ").Append(value).Append("\r\n");
            }
        }

        if (!response.CloseWithoutContentLength)
        {
            builder.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        }

        builder.Append("Connection: close\r\n\r\n");
        var headerBytes = Encoding.ASCII.GetBytes(builder.ToString());
        await stream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        // Graceful close: a bare Dispose can RST the socket and discard the buffered response.
        client.Client.Shutdown(SocketShutdown.Send);
    }
}
