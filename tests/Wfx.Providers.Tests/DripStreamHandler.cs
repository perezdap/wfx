using System.Net;
using System.Text;

namespace Wfx.Providers.Tests;

/// <summary>
/// Emits each server-sent-event chunk after a fixed delay, so a stream can run
/// longer in total than any single gap between events.
/// </summary>
internal sealed class DripStreamHandler(IReadOnlyList<string> chunks, TimeSpan gap) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var content = new StreamContent(new DripStream(chunks, gap));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }

    private sealed class DripStream(IReadOnlyList<string> chunks, TimeSpan gap) : Stream
    {
        private int _next;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_next >= chunks.Count)
            {
                return 0;
            }

            await Task.Delay(gap, cancellationToken).ConfigureAwait(false);
            var bytes = Encoding.UTF8.GetBytes(chunks[_next]);
            _next++;
            if (bytes.Length > buffer.Length)
            {
                throw new InvalidOperationException("Test chunk exceeds the read buffer.");
            }

            bytes.CopyTo(buffer);
            return bytes.Length;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
