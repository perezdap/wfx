using System.Net;
using System.Text;

namespace Wfx.Providers.Tests;

/// <summary>
/// Returns a fixed response body so transports can be exercised without a live
/// model endpoint, and records the request body for request-shape assertions.
/// </summary>
internal sealed class StubHandler(HttpStatusCode statusCode, string content, string mediaType) : HttpMessageHandler
{
    public string RequestBody { get; private set; } = string.Empty;

    public Uri? RequestUri { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestUri = request.RequestUri;
        RequestBody = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(content, Encoding.UTF8, mediaType)
        };
    }
}
