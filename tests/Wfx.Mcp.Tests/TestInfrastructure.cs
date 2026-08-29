using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Wfx.Mcp;

namespace Wfx.Mcp.Tests;

/// <summary>
/// An in-process stand-in for the stdio transport: two anonymous pipes wired so the client
/// writes where the fake server reads and vice versa. Keeps protocol tests deterministic
/// and free of child processes.
/// </summary>
internal sealed class TestPipes : IDisposable
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly AnonymousPipeServerStream _clientToServer = new(PipeDirection.Out, HandleInheritability.None);
    private readonly AnonymousPipeServerStream _serverToClient = new(PipeDirection.In, HandleInheritability.None);
    private readonly AnonymousPipeClientStream _serverIn;
    private readonly AnonymousPipeClientStream _serverOut;
    private bool _disposed;

    public TestPipes()
    {
        _serverIn = new AnonymousPipeClientStream(PipeDirection.In, _clientToServer.ClientSafePipeHandle);
        _serverOut = new AnonymousPipeClientStream(PipeDirection.Out, _serverToClient.ClientSafePipeHandle);
        ServerReader = new StreamReader(_serverIn, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        ServerWriter = new StreamWriter(_serverOut, Utf8NoBom, bufferSize: -1, leaveOpen: true) { AutoFlush = true };
        ClientWriter = new StreamWriter(_clientToServer, Utf8NoBom, bufferSize: -1, leaveOpen: true) { AutoFlush = true };
        ClientReader = new StreamReader(_serverToClient, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
    }

    public TextReader ServerReader { get; }

    public TextWriter ServerWriter { get; }

    public TextWriter ClientWriter { get; }

    public TextReader ClientReader { get; }

    /// <summary>Closes the server's write end, simulating the server process exiting.</summary>
    public void CloseServerSide()
    {
        ServerWriter.Dispose();
        _serverOut.Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeQuietly(ClientWriter);
        DisposeQuietly(ClientReader);
        DisposeQuietly(ServerReader);
        DisposeQuietly(ServerWriter);
        DisposeQuietly(_serverIn);
        DisposeQuietly(_serverOut);
        DisposeQuietly(_clientToServer);
        DisposeQuietly(_serverToClient);
    }

    private static void DisposeQuietly(IDisposable disposable)
    {
        try
        {
            disposable.Dispose();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // The pipe may already be closed; teardown order must not fail the test.
        }
    }
}

/// <summary>
/// A scripted JSON-RPC peer: reads newline-delimited requests and answers each method with
/// whatever raw line the test supplies. Handlers return the full response line (they decide
/// the id and any malformation), or null to stay silent for notifications.
/// </summary>
internal static class FakeMcpServer
{
    public static async Task RunAsync(
        TextReader input,
        TextWriter output,
        Func<string, JsonElement, string?> handler)
    {
        while (true)
        {
            string? line;
            try
            {
                line = await input.ReadLineAsync(CancellationToken.None);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            if (line is null)
            {
                return;
            }

            if (line.Length == 0)
            {
                continue;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                var method = root.TryGetProperty("method", out var methodElement) &&
                    methodElement.ValueKind == JsonValueKind.String
                    ? methodElement.GetString()!
                    : string.Empty;
                var response = handler(method, root);
                if (response is null)
                {
                    continue;
                }

                // Newline-delimited framing: handlers must return one frame per response.
                // A returned string with an embedded newline deliberately sends several
                // frames (the malformed-frame tests rely on that).
                try
                {
                    await output.WriteLineAsync(response.AsMemory(), CancellationToken.None);
                    await output.FlushAsync(CancellationToken.None);
                }
                catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>Builds a well-formed response line echoing the request id.</summary>
    public static string Response(JsonElement request, string resultJson) =>
        $"{{\"jsonrpc\":\"2.0\",\"id\":{request.GetProperty("id").GetRawText()},\"result\":{resultJson}}}";

    /// <summary>Builds a JSON-RPC error response echoing the request id.</summary>
    public static string Error(JsonElement request, int code, string message) =>
        $"{{\"jsonrpc\":\"2.0\",\"id\":{request.GetProperty("id").GetRawText()},\"error\":{{\"code\":{code},\"message\":\"{message}\"}}}}";

    public static long Id(JsonElement request) => request.GetProperty("id").GetInt64();
}

internal sealed class RecordingDisposable : IAsyncDisposable
{
    public int DisposeCount { get; private set; }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        return ValueTask.CompletedTask;
    }
}
