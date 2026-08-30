using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Wfx.Mcp;

/// <summary>
/// The interactive redirect: reserves a loopback port, opens the system browser at the
/// authorization URL, and serves one callback request with a "you can close this window"
/// page. A bare TCP listener is used instead of <see cref="HttpListener"/> because http.sys
/// requires URL ACL reservations an unprivileged user does not have.
/// </summary>
internal sealed class McpLoopbackBrowserRedirect : IMcpAuthorizationRedirect, IDisposable
{
    private readonly TcpListener _listener;
    private readonly Action<Uri> _openBrowser;

    /// <param name="openBrowser">Overrides the system-browser launch; tests pass a no-op and drive
    /// the loopback callback with an HTTP client instead of opening a real browser.</param>
    public McpLoopbackBrowserRedirect(Action<Uri>? openBrowser = null)
    {
        _openBrowser = openBrowser ?? OpenBrowser;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        RedirectUri = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/callback");
    }

    public Uri RedirectUri { get; }

    public async Task<Uri> WaitForCallbackAsync(Uri authorizationUrl, CancellationToken cancellationToken)
    {
        _openBrowser(authorizationUrl);
        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                _listener.Stop();
            }
            catch (ObjectDisposedException)
            {
            }
        });
        using var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
        var stream = client.GetStream();
        var requestLine = await ReadRequestLineAsync(stream, cancellationToken).ConfigureAwait(false);
        var path = requestLine.Split(' ') is { Length: >= 2 } parts ? parts[1] : "/";
        var body = Encoding.UTF8.GetBytes(
            "<html><body><p>wfx: sign-in complete. You can close this window.</p></body></html>");
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        // A bare Dispose would RST the socket and could discard the buffered page before the
        // browser reads it; shut down gracefully first.
        client.Client.Shutdown(SocketShutdown.Send);
        return new Uri($"http://127.0.0.1:{RedirectUri.Port}{path}");
    }

    public void Dispose() => _listener.Stop();

    private static void OpenBrowser(Uri url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url.ToString())
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // No browser available; the CLI prints the URL so the user can open it manually.
        }
    }

    private static async Task<string> ReadRequestLineAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var buffer = new byte[1];
        while (builder.Length < 16 * 1024)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var ch = (char)buffer[0];
            if (ch == '\n')
            {
                break;
            }

            if (ch != '\r')
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }
}
