using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;

namespace Wfx.Providers;

/// <summary>
/// The HTTP half of an OpenAI-style streaming call: endpoint composition,
/// authorization, retry of transient statuses, request timeout, secret
/// redaction, and server-sent-event framing. Protocol-specific JSON stays in
/// the transports that own it.
/// </summary>
internal sealed class SseHttpChannel
{
    private const int MaxRetries = 3;

    private readonly HttpClient _httpClient;
    private readonly OpenAiProviderOptions _options;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    public SseHttpChannel(HttpClient httpClient, OpenAiProviderOptions options, Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _httpClient = httpClient;
        _options = options;
        _delayAsync = delayAsync ?? Task.Delay;
        if (!_options.BaseUri.IsAbsoluteUri)
        {
            throw new ArgumentException("Provider base URI must be absolute.", nameof(options));
        }

        if (httpClient.Timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentException(
                "HttpClient.Timeout must be Timeout.InfiniteTimeSpan; the channel applies the configured timeout to each wait instead of capping the whole stream.",
                nameof(httpClient));
        }
    }

    public HttpRequestMessage CreateRequest(string relativePath, byte[] body)
    {
        var endpoint = new Uri(_options.BaseUri.ToString().TrimEnd('/') + relativePath, UriKind.Absolute);
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
        httpRequest.Headers.UserAgent.ParseAdd("wfx/0.1");
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }

        foreach (var header in _options.Headers)
        {
            if (header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Use ApiKey rather than an Authorization custom header.");
            }

            httpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        httpRequest.Content = new ByteArrayContent(body);
        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8"
        };
        return httpRequest;
    }

    /// <summary>
    /// Yields the payload of every <c>data:</c> line in the response stream,
    /// stopping at <c>[DONE]</c>. Throws when the stream carries no data event.
    /// Transient statuses (429 and 5xx) are retried with bounded, jittered
    /// backoff, honoring <c>Retry-After</c> when the endpoint sends one. The
    /// request factory runs once per attempt because sent messages cannot be
    /// replayed. The configured timeout bounds each wait, not the whole stream:
    /// each attempt (headers plus any backoff) gets a fresh window, and so does
    /// every subsequent line read.
    /// </summary>
    public async IAsyncEnumerable<string> ReadDataEventsAsync(
        Func<HttpRequestMessage> createRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        Func<HttpStatusCode, string, bool>? retryRejectedResponse = null)
    {
        using var timeoutSource = new CancellationTokenSource(_options.Timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        using var response = await SendWithRetriesAsync(
            createRequest,
            timeoutSource,
            linkedSource.Token,
            cancellationToken,
            retryRejectedResponse).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(linkedSource.Token).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var sawData = false;
        while (await ReadLineOrTimeoutAsync(reader, timeoutSource, cancellationToken, linkedSource.Token).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var data = line[5..].TrimStart();
            if (data.Equals("[DONE]", StringComparison.Ordinal))
            {
                break;
            }

            if (data.Length == 0)
            {
                continue;
            }

            sawData = true;
            yield return data;
        }

        if (!sawData)
        {
            throw new ProviderProtocolException("The provider stream ended without any data events.");
        }
    }

    private async Task<string?> ReadLineOrTimeoutAsync(
        StreamReader reader,
        CancellationTokenSource timeoutSource,
        CancellationToken cancellationToken,
        CancellationToken linkedToken)
    {
        timeoutSource.CancelAfter(_options.Timeout);
        try
        {
            return await reader.ReadLineAsync(linkedToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutSource.IsCancellationRequested)
        {
            throw new TimeoutException($"Model stream stalled: no data received for {_options.Timeout}.");
        }
    }

    private async Task<HttpResponseMessage> SendWithRetriesAsync(
        Func<HttpRequestMessage> createRequest,
        CancellationTokenSource timeoutSource,
        CancellationToken linkedToken,
        CancellationToken cancellationToken,
        Func<HttpStatusCode, string, bool>? retryRejectedResponse)
    {
        var transientAttempts = 0;
        while (true)
        {
            timeoutSource.CancelAfter(_options.Timeout);
            using var request = createRequest();
            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutSource.IsCancellationRequested)
            {
                throw new TimeoutException($"Model endpoint did not respond within the {_options.Timeout} timeout.");
            }

            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            using (response)
            {
                if (IsTransient(response.StatusCode) && transientAttempts < MaxRetries)
                {
                    var delay = GetRetryAfter(response) ?? ComputeBackoff(transientAttempts);
                    transientAttempts++;
                    try
                    {
                        await _delayAsync(delay, linkedToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutSource.IsCancellationRequested)
                    {
                        throw new TimeoutException($"Model request exceeded the {_options.Timeout} timeout.");
                    }

                    continue;
                }

                var error = await ReadBoundedAsync(response.Content, 64 * 1024, linkedToken).ConfigureAwait(false);
                if (retryRejectedResponse?.Invoke(response.StatusCode, error) is true)
                {
                    continue;
                }

                throw new HttpRequestException(
                    $"Model endpoint returned {(int)response.StatusCode} {response.ReasonPhrase}: {Redact(error)}",
                    null,
                    response.StatusCode);
            }
        }
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        (int)statusCode == 429 || (int)statusCode is >= 500 and <= 599;

    /// <summary>
    /// The endpoint's requested wait, bounded only by the overall request
    /// timeout. A malformed header reads as absent, so backoff applies.
    /// </summary>
    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (retryAfter?.Date is { } date)
        {
            var delay = date - DateTimeOffset.UtcNow;
            return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        }

        return null;
    }

    /// <summary>
    /// Full jitter between zero and a cap that grows 1s, 4s, 16s per retry.
    /// </summary>
    private static TimeSpan ComputeBackoff(int attempt)
    {
        var capTicks = (long)(TimeSpan.FromSeconds(1).Ticks * Math.Pow(4, attempt));
        return TimeSpan.FromTicks(Random.Shared.NextInt64(capTicks));
    }

    public string Redact(string value)
    {
        var secrets = _options.Headers.Values
            .Append(_options.ApiKey)
            .Where(static secret => !string.IsNullOrEmpty(secret))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(static secret => secret!.Length);
        foreach (var secret in secrets)
        {
            value = value.Replace(secret!, "[REDACTED]", StringComparison.Ordinal);
        }

        return value;
    }

    private static async Task<string> ReadBoundedAsync(HttpContent content, int maxCharacters, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var buffer = new char[maxCharacters];
        var count = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
        return new string(buffer, 0, count);
    }
}
