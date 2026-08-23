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

internal sealed record StubResponse(HttpStatusCode StatusCode, string Content, string MediaType);

internal sealed class SequenceStubHandler(IEnumerable<StubResponse> responses) : HttpMessageHandler
{
    private readonly Queue<StubResponse> _responses = new(responses);

    public List<string> RequestBodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestBodies.Add(request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken));
        if (_responses.Count == 0)
        {
            throw new InvalidOperationException("The queue ran out of responses.");
        }

        var response = _responses.Dequeue();
        return new HttpResponseMessage(response.StatusCode)
        {
            Content = new StringContent(response.Content, Encoding.UTF8, response.MediaType)
        };
    }
}
